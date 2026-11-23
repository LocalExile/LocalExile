using GameServer.Modding;

namespace GameServer.Modules.Ritual;

/// <summary>
/// Ritual as a LocalExile module: the altars of the core game's maps, listed in the leagues' modules.json as Anarchy and
/// Heist are. A new instance rolls its altars when its monsters are placed (<see cref="GameEvents.AreaPopulated"/>); the
/// encounter, <see cref="RitualEncounter"/>, belongs to the instance, so every client of it sees and drives the same altars.
/// This file only hooks it: the placement, what an entering client is told, the area tick and a click on an altar.
/// </summary>
public sealed class RitualModule : IModule
{
    readonly RitualEncounter _encounter = new();

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    /// <summary>This module's game data, built when it loads: before the Gateway listens. Tests that use the module's
    /// tables call it once.</summary>
    public static void LoadData() => RitualTables.LoadGameData();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        string id = module.Manifest.Id;
        module.Lifetime.Own(new Cleanup(_encounter.Clear));
        GameEvents.AreaPopulated.Add(id, async area =>
        {
            if (_encounter.Populate(area) is { } why) module.Log($"Ritual: none in {area.Player.Layout.Id} ({why})");
            else await _encounter.Placed(area.Player);
        });
        GameEvents.EnterMessages.Add(id, _encounter.EnterMessages);
        GameEvents.PlayerEntered.Add(id, _encounter.Entered);
        GameEvents.AreaTick.Add(id, start => MapDevices.IsMapArea(start.Layout.Hash)
            ? (player, seconds) => _encounter.Of(player) is null ? Task.CompletedTask : _encounter.Advance(player, seconds) : null);
        GameEvents.ObjectUse.Add(id, _encounter.OnUse);
    }
}
