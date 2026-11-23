/// <summary>
/// The area's Stats phase: once a frame, every entity a change marked has its stat graph stepped (StatGraph.cs), once,
/// for the frame that follows; reads in between see the last step. A monster's marks name the layer that changed (its
/// mods, encounter, buff, curse or a temporary source) and its world lists it (MonsterTable.TakeMarked), so only marked
/// monsters are touched; one the phase has stepped is stepped only here (StatsByFrame). A player's stats the same way
/// (PlayerStatsCache.cs: StepPlayerStats), with the character panel published once a frame.
/// </summary>
public static partial class Game
{
    /// <summary>Takes the framework's stat-change hand-over (Player.StatsChanged) for this module's lifetime.</summary>
    public static void LoadStats(GameServer.Modding.IModuleContext module)
    {
        Action<Player, StatInput> mark = PlayerStatInputChanged;
        Player.StatsChanged = mark;
        module.Lifetime.Own(new StatsHandOver(mark));
    }
    sealed class StatsHandOver(Action<Player, StatInput> mark) : IDisposable
    {
        public void Dispose() { if (Player.StatsChanged == mark) Player.StatsChanged = null; }
    }

    public static void StartStats(GameArea area, Player player, object source)
    {
        var monsters = player.Area.Monsters;
        var frames = player.Frames;
        area.Events.On<StatBuffsChanged>(e =>
        {
            foreach (var peer in frames.Players())
                if (peer.Id == e.Entity.Value) PlayerStatBuffsChanged(peer);
        }, source);
        area.Systems.Add("stats", AreaPhase.Stats, _ =>
        {
            // Players first, in the order they joined; then the monsters.
            foreach (var peer in frames.Players()) StepPlayerStats(peer);
            foreach (var m in monsters.TakeMarked())
            {
                m.StatsByFrame = true;
                if (m.StatsDirty) m.RebuildStats();
            }
        }, source: source);
    }
}
