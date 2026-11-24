using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>The Legion encounter: the Timeless Monolith and its timer, the two armies in stasis around it, their break-out by the
/// player's hits and the release when the timer ends. Loads the Legion client tables (<see cref="LegionData"/>) and registers the
/// monolith (<see cref="LegionMonolith"/>); the army is planned in LegionArmy and spawned in LegionStasis.</summary>
public sealed class LegionModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        var data = LegionData.Reload();
        module.Lifetime.Own(new Cleanup(LegionData.Unload));
        LegionMonolith.Register(module);
        module.Log($"{data.Factions.Count} factions, {data.Ranks.Count} ranks, {data.MonsterVarieties.Count} monster varieties, {data.MonsterCounts.Count} monster counts, {data.ChestTypes.Count} chest types, {data.ChestCounts.Count} chest counts, {data.Balances.Count} balance rows");
    }

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }
}
