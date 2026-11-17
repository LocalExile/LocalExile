using GameServer.Modding;

namespace GameServer.Modules.Strongbox;

/// <summary>
/// Strongboxes, the Ambush league mechanic of the core game: an area's league roll places strongboxes
/// (<see cref="StrongboxRules"/>), a walk-up click or a chest-opening warcry opens one, its guards come for the player and
/// its items drop (<see cref="StrongboxManager"/>, <see cref="StrongboxLoot"/>). This module only wires the framework to
/// the manager.
/// </summary>
public sealed class StrongboxModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        string id = module.Manifest.Id;
        GameEvents.AreaGenerated.Add(id, StrongboxManager.SpawnArea);
        GameEvents.ObjectUse.Add(id, StrongboxManager.Use);
        GameEvents.ChestsOpened.Add(id, StrongboxManager.OpenNear);
        module.Lifetime.Own(StrongboxManager.Lifetime());
    }
}
