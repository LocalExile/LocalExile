/// <summary>
/// The area's Stats phase: once a frame, every entity whose stats a change marked dirty (SpawnedMonster.MarkStatsDirty:
/// its mods, encounter, buff and temporary stats, curses) has them built again, once, for the frame that follows; reads
/// in between see the last build. A monster the phase has seen is built only here (StatsByFrame).
/// </summary>
public static partial class Game
{
    public static void StartStats(GameArea area, ClientState conn, object source)
    {
        var monsters = conn.Monsters;
        area.Systems.Add("stats", AreaPhase.Stats, _ =>
        {
            lock (monsters)
                foreach (var m in monsters.Values)
                {
                    m.StatsByFrame = true;
                    if (m.StatsDirty) m.RebuildStats();
                }
        }, source: source);
    }
}
