using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Blight;

/// <summary>Blight's pump defence (<see cref="BlightEncounter"/>): its tables, towers, waves, chests, Cassia and hooks.</summary>
public sealed class BlightModule : IModule
{
    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    /// <summary>This module's game data, built when it loads, before the Gateway listens. Tests that use the module call it once.</summary>
    public static void LoadData() => BlightTables.Load();

    public void Load(IModuleContext module)
    {
        LoadData();
        string id = module.Manifest.Id;
        var encounter = new BlightEncounter();
        module.Lifetime.Own(new Cleanup(() =>
        {
            encounter.Runs.Clear();
            BlightTowers.ClearAllImbuements();
        }));
        GameEvents.PlayerEntered.Add(id, encounter.Enter);
        // Any map or campaign area whose generated level places a BlightPump runs the defence; non-Blight areas skip in O(1).
        GameEvents.AreaTick.Add(id, start => encounter.MayHaveDefence(start) ? (player, _) => encounter.Step(player) : null);
        GameEvents.ObjectUse.Add(id, encounter.Use);
        GameEvents.Packet(InstanceBlight.TowerBuildId).Add(id, (player, bytes) => encounter.Build(player, bytes));
        GameEvents.Kill.Add(id, kill => kill.Later(() => encounter.Killed(kill)));
        GameEvents.FinalStatsBuilding.Add(id, BlightTowers.ApplyPlayerImbuement);
        GameEvents.PlayerLeaving.Add(id, (player, _) => BlightTowers.ClearPlayerImbuement(player));
    }
}
