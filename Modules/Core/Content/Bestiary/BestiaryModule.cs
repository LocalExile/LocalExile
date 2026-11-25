using GameServer.Modding;

namespace GameServer.Modules.Bestiary;

/// <summary>
/// Bestiary: Einhar's mission in map areas. A new instance rolls it from the map's master-mission stats
/// (<see cref="BestiaryEncounter.Rolled"/>); the area then holds the EinharMissions row's red beast and yellow beasts, captured
/// as they are killed (<see cref="BestiaryEncounter"/>). Einhar arrives when a beast is first brought low and leaves after the
/// last. Not wired yet: the 0x15f mission tracker (the text of its Failed entry is not known), Einhar's dialogue, the net throw,
/// the Menagerie and the Blood Altar craft.
/// </summary>
public sealed class BestiaryModule : IModule
{
    readonly BestiaryEncounter encounter = new();

    /// <summary>This module's game data, built once when it loads.</summary>
    public static void LoadData() => BestiaryTables.LoadGameData();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        string id = module.Manifest.Id;
        module.Lifetime.Own(new Cleanup(encounter.Clear));
        GameEvents.AreaPopulated.Add(id, encounter.Place);
        GameEvents.PlayerEntered.Add(id, encounter.Entered);
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, seconds) => encounter.Has(player) ? encounter.Advance(player, seconds) : Task.CompletedTask));
        GameEvents.PlayerLeaving.Add(id, (player, _) => encounter.Leaving(player));
        GameEvents.Kill.Add(id, encounter.Killed);
    }

    sealed class Cleanup(Action clean) : IDisposable
    {
        public void Dispose() => clean();
    }
}
