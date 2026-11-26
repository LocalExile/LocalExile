using GameServer.Modding;

namespace GameServer.Modules.Abyss;

/// <summary>
/// Abyss: a crack in the ground that opens into a pit. A new instance rolls CoreLeagues row 9 at its WorldAreaLeagueChances
/// +0x44 chance and the map's stats (<see cref="AbyssEncounter.Rolled"/>) when its monsters are placed and, with a roll,
/// plans one encounter on the lattice around an encounter spot (<see cref="AbyssEncounter"/>). The encounter belongs to the
/// area instance. Agents/docs/game/league-encounters.md.
/// </summary>
public sealed class AbyssModule : IModule
{
    readonly AbyssEncounter _encounter = new();

    public static void LoadData() => AbyssTables.LoadGameData();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        string id = module.Manifest.Id;
        module.Lifetime.Own(new Cleanup(_encounter.Clear));
        module.Lifetime.Own(RegisterDeliriumPauseSource(p => _encounter.Of(p) is { } s
            && (s.Stage == AbyssEncounter.Stage.Wave || (s.Stage == AbyssEncounter.Stage.Arms && s.Arms.Any(a => a.StartedAt is not null)))));
        GameEvents.AreaPopulated.Add(id, area =>
        {
            if (_encounter.Place(area) is { } why) module.Log(why);
            return Task.CompletedTask;
        });
        GameEvents.PlayerEntered.Add(id, _encounter.Entered);
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, seconds) =>
            _encounter.Of(player) is null ? Task.CompletedTask : _encounter.Advance(player, seconds)));
        GameEvents.Kill.Add(id, _encounter.Killed);
        GameEvents.ObjectUse.Add(id, _encounter.OnUse);
        GameEvents.ChestsOpened.Add(id, _encounter.OpenNear);
    }

    sealed class Cleanup(Action clean) : IDisposable
    {
        public void Dispose() => clean();
    }
}
