using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Crucible league (3.21): in-zone Crucible Forges, Weapon Passive Trees on weapons and shields,
/// Magmatic Ore, Igneous/Crystalline Geodes → Primeval/Primordial Remnants, and the Forge of the Titans.
/// </summary>
public sealed class CrucibleModule : IModule
{
    sealed record Choices(double ExperienceMultiplier, double GeodeDropRate);

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    public static void LoadData()
    {
        CrucibleTables.LoadGameData();
        CrucibleMonsters.LoadGameData();
    }

    public void Load(IModuleContext module)
    {
        LoadData();
        var choices = module.Settings<Choices>();
        string id = module.Manifest.Id;
        module.Log($"Crucible loaded (XP ×{choices.ExperienceMultiplier:0.##}, geode drop rate ×{choices.GeodeDropRate:0.##})");

        module.Lifetime.Own(new Cleanup(() =>
        {
            CrucibleForgeOfTheTitans.Clear();
            CrucibleEncounter.Clear();
        }));
        (CrucibleEncounter.ExperienceMultiplier, CrucibleEncounter.GeodeDropRate) = (choices.ExperienceMultiplier, choices.GeodeDropRate);

        // 1. Register CrucibleLifeScalingPerLevel monster life scaling and the Forge's damage stats.
        module.Lifetime.Own(MonsterModEffects.HandOverMore([CrucibleTables.LevelMoreLifeStat],
            [CrucibleTables.MonsterMoreDamageStat], [CrucibleTables.MonsterDamageTakenStat]));

        // 2. Inject map_crucible_league (1-based stat 17912) so ExtraTerrainFeatures rolls "Crucible" rooms.
        InstanceEvents.AreaStats.Add(id, _ => new Dictionary<uint, int> { [CrucibleTables.LeagueStat] = 1 });

        // 3. Map Device: attach Remnant Domain 33 mods onto Forge of the Titans openings.
        GameEvents.MapOpening.Add(id, CrucibleRemnants.OnMapOpening);

        // 4. Right-click Geode -> Primeval/Primordial Remnant (C→S 0x58).
        GameEvents.ItemUse.Add(id, CrucibleRemnants.TryOpenGeode);

        // 5. Magmatic Ore currency application (C→S 0x71 / 0x72).
        GameEvents.CurrencyApply.Add(id, (player, target, currencyType, currencyItem, rng) =>
            CrucibleItemActions.ApplyCurrency(player, target, currencyType, currencyItem, rng));

        // 6. Crucible SellBonus vendor rewards.
        GameEvents.VendorQuote.Add(id, CrucibleItemActions.OnVendorQuote);

        // 7. C→S 0x0116 Orb of Scouring unallocation of deepest allocated weapon passive node.
        GameEvents.Packet(InstanceCrucible.WeaponPassiveUnallocateId).Add(id, CrucibleItemActions.HandleUnallocate);

        // 8. Forge of the Titans: Combining Bench (0x299 -> 0x29a).
        GameEvents.Packet(InstanceCrucible.CombineRequestId).Add(id, CrucibleForgeOfTheTitans.HandleCombine);

        // 9. In-zone Crucible Forge: native CrucibleChannel skill (0x1f29) + Forge stats & proximity/waves.
        GameEvents.Stats.Add(id, CrucibleEncounter.OnStats);
        GameEvents.GrantedSkills.Add(id, CrucibleEncounter.GrantedSkills);
        GameEvents.SkillBarBuilding.Add(id, CrucibleEncounter.OnSkillBarBuilding);
        GameEvents.Packet(InstanceProtocol.UseSkillAtLocationId).Add(id, (player, bytes) =>
        {
            var use = InstanceProtocol.ReadUseSkillAtLocation(bytes.AsSpan());
            return CrucibleEncounter.HandleChannelSkill(player, use.SkillId, use.Slot, use.Flags);
        });
        GameEvents.Packet(InstanceTransfer.UseSkillOnObjectId).Add(id, (player, bytes) =>
        {
            var use = InstanceTransfer.ReadUseSkillOnObject(bytes.AsSpan());
            return CrucibleEncounter.HandleChannelSkill(player, use.SkillId, use.Sequence, use.Flags);
        });
        // A channeller's aim updates (C→S 0x0197 / 0x0198) are taken while they hold.
        Task<bool> Holding(Player player, byte[] _) => Task.FromResult(CrucibleEncounter.IsHolding(player));
        GameEvents.Packet(InstanceProtocol.ChannelUpdateId).Add(id, Holding);
        GameEvents.Packet(InstanceProtocol.ChannelUpdateAltId).Add(id, Holding);
        GameEvents.Packet(InstanceProtocol.ChannelEndId).Add(id, (player, _) =>
            CrucibleEncounter.HandleChannelEnd(player));
        // Lockstep equivalents (0x018e/0x018f start, 0x0195/0x0196 release) for local/auto-lockstep clients.
        Task<bool> LockstepChannel(Player player, ushort skillId, ushort flags)
        {
            // Another skill goes on to the game; the encounter only ends a hold on it.
            if (skillId != InstanceCrucible.CrucibleChannelSkillHash) return CrucibleEncounter.HandleChannelSkill(player, skillId, 0, flags);
            // A held popup click repeats the start every frame: only a new channel takes a sequence.
            if (!CrucibleEncounter.IsHolding(player)) player.LockstepSequence = (ushort)((player.LockstepSequence + 1) & 0x7fff);
            return CrucibleEncounter.HandleChannelSkill(player, skillId, (ushort)(player.LockstepSequence | 0x8000), flags);
        }
        GameEvents.Packet(InstanceProtocol.LockstepMoveStartId).Add(id, (player, bytes) =>
        {
            var click = InstanceProtocol.ReadLockstepMoveStart(bytes);
            return LockstepChannel(player, click.SkillId, click.Flags);
        });
        GameEvents.Packet(InstanceProtocol.LockstepUseOnObjectId).Add(id, (player, bytes) =>
        {
            var use = InstanceProtocol.ReadLockstepUseOnObject(bytes);
            return LockstepChannel(player, use.SkillId, use.Flags);
        });
        // The releases are only watched: 0x0192 and 0x0195 are also those of a Move held since before the channel, so
        // the game still gets them.
        GameEvents.Packet(InstanceProtocol.LockstepMoveStopId).Add(id, async (player, _) =>
        {
            await CrucibleEncounter.HandleChannelEnd(player);
            return false;
        });
        GameEvents.Packet(InstanceProtocol.LockstepSkillStopId).Add(id, async (player, bytes) =>
        {
            if (InstanceProtocol.ReadLockstepSkillStop(bytes) == InstanceCrucible.CrucibleChannelSkillHash)
                await CrucibleEncounter.HandleChannelEnd(player);
            return false;
        });

        // The pick in the Forge popup, from a client changed to send it (C→S 0x02ca for CrucibleRules).
        GameEvents.Packet(InstanceRulesets.ActionId).Add(id, CrucibleEncounter.HandleRulesetAction);

        // 10. Rulesets, area generation, player entry, world object interaction, ticks, kills and leaving.
        GameEvents.EnterMessages.Add(id, player =>
        {
            if (CrucibleTables.IsForgeArea(player.Layout.Id))
                return [("0x001f ruleset CrucibleMapRules", InstanceProtocol.AddRuleset(InstanceCrucible.CrucibleMapRulesetRow))];
            if (CrucibleEncounter.HasForgePlacement(player))
                return [("0x001f ruleset CrucibleRules", InstanceProtocol.AddRuleset(InstanceCrucible.CrucibleRulesetRow))];
            return [];
        });
        GameEvents.AreaGenerated.Add(id, async player =>
        {
            await CrucibleForgeOfTheTitans.AreaGenerated(player);
            await CrucibleEncounter.AreaGenerated(player);
        });
        GameEvents.PlayerEntered.Add(id, async player =>
        {
            CrucibleForgeOfTheTitans.PlayerEntered(player);
            await CrucibleEncounter.PlayerEntered(player);
        });

        GameEvents.ObjectUse.Add(id, CrucibleForgeOfTheTitans.UseObject);
        GameEvents.AreaTick.Add(id, player => CrucibleEncounter.HasForgePlacement(player) ? CrucibleEncounter.Tick : null);
        GameEvents.Kill.Add(id, CrucibleEncounter.OnKill);
        GameEvents.PlayerLeaving.Add(id, (player, _) => CrucibleEncounter.OnPlayerLeaving(player));
    }
}
