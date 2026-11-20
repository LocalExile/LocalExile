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

        module.Lifetime.Own(new Cleanup(CrucibleForgeOfTheTitans.Clear));

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

        // 9. Area generation, world object interaction, and Forge of the Titans drops.
        GameEvents.AreaGenerated.Add(id, async conn =>
        {
            await CrucibleForgeOfTheTitans.Enter(conn);
        });

        GameEvents.ObjectUse.Add(id, async (conn, use, requestedAt) =>
            await CrucibleForgeOfTheTitans.UseObject(conn, use, requestedAt));

        GameEvents.Drops.Add(id, CrucibleForgeOfTheTitans.OnDrops);
    }
}
