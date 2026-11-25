using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Blight;

/// <summary>Blight's pump defence (<see cref="BlightEncounter"/>): its tables, and the game events that drive it.</summary>
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
        module.Lifetime.Own(new Cleanup(encounter.Runs.Clear));
        GameEvents.PlayerEntered.Add(id, encounter.Enter);
        // Only a map can hold a pump, and only its connection that runs the instance's monsters steps it.
        GameEvents.AreaTick.Add(id, start => MapDevices.IsMapArea(start.Layout.Hash) ? (player, _) => encounter.Step(player) : null);
        GameEvents.ObjectUse.Add(id, encounter.Use);
        GameEvents.Packet(InstanceBlight.TowerBuildId).Add(id, (player, bytes) => encounter.Build(player, bytes));
        GameEvents.Kill.Add(id, kill => kill.Later(() => encounter.Killed(kill)));
    }
}
