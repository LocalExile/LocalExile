/// <summary>
/// The area's Stats phase: once a frame, every entity whose stats a change marked dirty (SpawnedMonster.MarkStatsDirty:
/// its mods, encounter, buff and temporary stats, curses) has them built again, once, for the frame that follows; reads
/// in between see the last build. A monster the phase has seen is built only here (StatsByFrame). A player's stats the
/// same way (PlayerStatsCache.cs: StepPlayerStats), with the character panel published once a frame.
/// </summary>
public static partial class Game
{
    /// <summary>Takes the framework's stat-change hand-over (ClientState.StatsChanged) for this module's lifetime.</summary>
    public static void LoadStats(GameServer.Modding.IModuleContext module)
    {
        Action<ClientState> mark = MarkPlayerStatsDirty;
        ClientState.StatsChanged = mark;
        module.Lifetime.Own(new StatsHandOver(mark));
    }
    sealed class StatsHandOver(Action<ClientState> mark) : IDisposable
    {
        public void Dispose() { if (ClientState.StatsChanged == mark) ClientState.StatsChanged = null; }
    }

    public static void StartStats(GameArea area, ClientState conn, object source)
    {
        var monsters = conn.Monsters;
        var frames = conn.Frames;
        area.Systems.Add("stats", AreaPhase.Stats, _ =>
        {
            // Players first, in the order they joined; then the monsters.
            foreach (var player in frames.Players()) StepPlayerStats(player);
            lock (monsters)
                foreach (var m in monsters.Values)
                {
                    m.StatsByFrame = true;
                    if (m.StatsDirty) m.RebuildStats();
                }
        }, source: source);
    }
}
