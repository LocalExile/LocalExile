using GameServer.Modding;

namespace GameServer.Modules.HeistCache;

/// <summary>
/// Smuggler's Caches as a LocalExile module (Modules/Core/Content/HeistCache, listed in the leagues'
/// modules.json next to Heist). The encounter is <see cref="HeistCacheEncounter"/>; this file only hooks it:
/// the area's population (when the league roll gives the instance a cache), a player entering, a click on a cache, and the
/// module's unload. It works in normal play with no setting and no dev command: the league roll gates it.
/// </summary>
public sealed class HeistCacheModule : IModule
{
    readonly HeistCacheEncounter _encounter = new();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        string id = module.Manifest.Id;
        module.Lifetime.Own(new Cleanup(_encounter.Clear));
        GameEvents.AreaPopulated.Add(id, area => _encounter.OnAreaPopulated(area));
        GameEvents.PlayerEntered.Add(id, player => _encounter.OnPlayerEntered(player));
        GameEvents.ObjectUse.Add(id, (player, use, at) => _encounter.OnUse(player, use, at));
    }

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }
}
