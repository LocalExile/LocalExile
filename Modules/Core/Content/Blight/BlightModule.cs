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
        GameEvents.AreaTick.Add(id, GameEvents.Always(async (player, seconds) =>
        {
            if (player.Instance is null || !encounter.Runs.TryGetValue(player.Instance, out _)) return;
            // One live client of the instance runs it.
            var runner = InGame.Keys.Where(c => !c.Controlled && ReferenceEquals(c.Instance, player.Instance) && c.PlayerLife > 0
                && c.Connection.PlayerSent && !c.LeavingArea && !c.Closed).MinBy(c => c.Id);
            if (!ReferenceEquals(runner, player) || player.Paused) return;
            await encounter.Tick(player);
        }));
        GameEvents.ObjectUse.Add(id, encounter.Use);
        GameEvents.Packet(InstanceBlight.TowerBuildId).Add(id, (player, bytes) => encounter.Build(player, bytes));
        GameEvents.Kill.Add(id, kill => kill.Later(() => encounter.Killed(kill)));
    }
}
