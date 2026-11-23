using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>The Legion encounter (monolith, two armies in stasis, break-out). This part only loads the Legion client
/// tables (<see cref="LegionData"/>); the encounter itself follows.</summary>
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
