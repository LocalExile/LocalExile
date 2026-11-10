using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Heist;

/// <summary>The hideout's Heist Locker.</summary>
public sealed class HeistModule : IModule
{
    const string LockerType = "Metadata/MiscellaneousObjects/Heist/HeistStash_Hideout";

    sealed class State { public uint Locker; }
    readonly ConditionalWeakTable<IPlayer, State> _states = new();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.HideoutReady.Add(module.Manifest.Id, async conn =>
        {
            var player = PlayerOf(conn);
            if (await module.World(player).SpawnHideoutStation(HideoutSlot.Small, LockerType, "heist-locker", "Heist Locker") is { } id)
                _states.GetOrCreateValue(player).Locker = id;
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (conn, use, at) =>
        {
            if (!_states.TryGetValue(PlayerOf(conn), out var state) || state.Locker == 0 || use.TargetId != state.Locker) return false;
            var world = module.World(PlayerOf(conn));
            if (await world.WalkUpToStation(state.Locker, at))
                await world.OpenWindow("heist", InstanceWaypoints.HeistLockerUi, "0x007b open native Heist Locker (HeistStashOpen)");
            return true;
        });
    }
}
