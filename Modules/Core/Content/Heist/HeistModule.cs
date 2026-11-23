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
        GameEvents.HideoutReady.Add(module.Manifest.Id, async player =>
        {
            var modPlayer = PlayerOf(player);
            if (await WorldOf(modPlayer).SpawnHideoutStation(HideoutSlot.Small, LockerType, "heist-locker", "Heist Locker") is { } id)
                _states.GetOrCreateValue(modPlayer).Locker = id;
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (player, use, at) =>
        {
            if (!_states.TryGetValue(PlayerOf(player), out var state) || state.Locker == 0 || use.TargetId != state.Locker) return false;
            var world = WorldOf(player);
            if (await world.WalkUpToStation(state.Locker, at))
                await world.OpenWindow("heist", InstanceWaypoints.HeistLockerUi, "0x007b open native Heist Locker (HeistStashOpen)");
            return true;
        });
    }
}
