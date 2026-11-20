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

    public void Load(IModuleContext module)
    {
        var choices = module.Settings<Choices>();
        string id = module.Manifest.Id;
        module.Log($"Crucible loaded (XP ×{choices.ExperienceMultiplier:0.##}, geode drop rate ×{choices.GeodeDropRate:0.##})");

        module.Lifetime.Own(new Cleanup(() =>
        {
            CrucibleForgeOfTheTitans.Clear();
            CrucibleEncounter.Clear();
        }));

        // 1. Register CrucibleLifeScalingPerLevel monster life scaling stat.
        module.Lifetime.Own(MonsterModEffects.HandOverMore([CrucibleTables.LevelMoreLifeStat], []));

        // 2. Inject map_crucible_league (1-based stat 17912) so ExtraTerrainFeatures rolls "Crucible" rooms.
        InstanceEvents.AreaStats.Add(id, _ =>
            Stats.Key(CrucibleTables.LeagueStat) is uint key ? new Dictionary<uint, int> { [key] = 1 } : null);

        // 3. Map Device: attach Remnant Domain 33 mods onto Forge of the Titans openings.
        GameEvents.MapOpening.Add(id, CrucibleRemnants.OnMapOpening);

        // 4. Right-click Geode -> Primeval/Primordial Remnant (C→S 0x58).
        GameEvents.ItemUse.Add(id, CrucibleRemnants.TryOpenGeode);

        // 5. Magmatic Ore currency application (C→S 0x71 / 0x72).
        GameEvents.CurrencyApply.Add(id, (conn, target, currencyType, rng) =>
            CrucibleItemActions.ApplyCurrency(conn, target, currencyType, rng, choices.ExperienceMultiplier));

        // 6. Crucible SellBonus vendor rewards.
        GameEvents.VendorQuote.Add(id, CrucibleItemActions.OnVendorQuote);

        // 7. C→S 0x0116 Orb of Scouring unallocation of deepest allocated weapon passive node.
        GameEvents.Packet(InstanceCrucible.WeaponPassiveUnallocateId).Add(id, CrucibleItemActions.HandleUnallocate);

        // 8. Forge of the Titans: Combining Bench (0x299 -> 0x29a) & Antique/Scouring Furnace (0x29b -> 0x29c).
        GameEvents.Packet(InstanceCrucible.CombineRequestId).Add(id, CrucibleForgeOfTheTitans.HandleCombine);
        GameEvents.Packet(InstanceCrucible.UniqueForgeRequestId).Add(id, CrucibleForgeOfTheTitans.HandleUniqueForge);

        // 9. In-zone Crucible Forge: native CrucibleChannel skill (0x5f29) + Forge stats & proximity/waves.
        GameEvents.Stats.Add(id, CrucibleEncounter.OnStats);
        GameEvents.GrantedSkills.Add(id, CrucibleEncounter.GrantedSkills);
        GameEvents.SkillBarBuilding.Add(id, CrucibleEncounter.OnSkillBarBuilding);
        GameEvents.Packet(InstanceProtocol.UseSkillAtLocationId).Add(id, (conn, bytes) =>
            CrucibleEncounter.HandleChannelSkill(conn, InstanceProtocol.ReadUseSkillAtLocation(bytes.AsSpan()).SkillId));
        GameEvents.Packet(InstanceTransfer.UseSkillOnObjectId).Add(id, (conn, bytes) =>
            CrucibleEncounter.HandleChannelSkill(conn, InstanceTransfer.ReadUseSkillOnObject(bytes.AsSpan()).SkillId));

        // 10. Area generation, world object interaction, ticks, kills, leaving, and drops.
        GameEvents.AreaGenerated.Add(id, async conn =>
        {
            await CrucibleForgeOfTheTitans.Enter(conn);
            await CrucibleEncounter.Enter(conn);
        });

        GameEvents.ObjectUse.Add(id, async (conn, use, requestedAt) =>
            await CrucibleForgeOfTheTitans.UseObject(conn, use, requestedAt)
            || await CrucibleEncounter.UseObject(conn, use, requestedAt));

        GameEvents.Tick.Add(id, (conn, dt) =>
            CrucibleEncounter.Tick(conn, dt, choices.ExperienceMultiplier, choices.GeodeDropRate));

        GameEvents.Kill.Add(id, kill =>
            CrucibleEncounter.OnKill(kill, choices.ExperienceMultiplier, choices.GeodeDropRate));

        GameEvents.PlayerLeaving.Add(id, (conn, _) =>
        {
            CrucibleEncounter.OnPlayerLeaving(conn);
            return Task.CompletedTask;
        });

        GameEvents.Drops.Add(id, CrucibleForgeOfTheTitans.OnDrops);
    }
}
