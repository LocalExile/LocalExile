using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>The Legion encounter: the Timeless Monolith, Legion Scarabs &amp; Atlas passives (including Timeless Conflict),
/// and the Domain of Timeless Conflict (2–5 Timeless Emblems). Loads the Legion client tables (<see cref="LegionData"/>)
/// and registers both map monoliths (<see cref="LegionMonolith"/>) and the Domain (<see cref="LegionDomain"/>).</summary>
public sealed class LegionModule : IModule
{
    /// <summary>Loads this module's static game data eagerly (called on module load and by Gateway test setup).</summary>
    public static void LoadData()
    {
        LegionData.Reload();
        LegionMonolith.LoadData();
        LegionDomain.LoadData();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        var data = LegionData.Current;
        module.Lifetime.Own(new Cleanup(() => { LegionDomain.UnloadData(); LegionMonolith.UnloadData(); LegionData.Unload(); }));
        LegionMonolith.Register(module);
        LegionDomain.Register(module);
        module.Log($"{data.Factions.Count} factions, {data.Ranks.Count} ranks, {data.MonsterVarieties.Count} monster varieties, {data.MonsterCounts.Count} monster counts, {data.ChestTypes.Count} chest types, {data.ChestCounts.Count} chest counts, {data.Balances.Count} balance rows, {data.DomainAreas.Count} Domain areas, {data.EmblemsByRow.Count} emblems");
    }

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }
}
