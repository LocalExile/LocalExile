/// <summary>
/// The monsters' damage over time (ignites, bleeds, poisons, contagions, essence drains) ticks once for the area, every
/// frame, instead of on the tick of the area's first player (which stopped while that player paused or left).
/// The tick still sends and credits as that player (the area's view), as a task of theirs; the next waits for it.
/// </summary>
public static partial class Game
{
    public static bool AreaRunsGame(ClientState conn) => AreaSetup.Runs(conn);

    public static void StartMonsterDamageOverTime(GameArea area, ClientState conn, object source)
    {
        var frames = conn.Frames;
        Task pass = Task.CompletedTask;
        area.Systems.Add("monster damage over time", AreaPhase.Ailments, a =>
        {
            if (!pass.IsCompleted || !CurrentGameWork || frames.View is not { } view || view.Closed || view.LeavingArea
                || view.Paused || !view.PlayerSent || view.PlayerUpdatesState is not PlayerUpdatesState updates
                || !PlayerUpdatesCurrent(view, updates)) return;
            var (remote, stream, ciphers) = view.Wire;
            pass = AsPlayer(view, updates, async () =>
            {
                await BurnIgnited(remote, stream, ciphers, view);
                await BleedMonsters(remote, stream, ciphers, view);
                await TickPoisons(remote, stream, ciphers, view);
                await TickContagions(remote, stream, ciphers, view);
                await TickEssenceDrains(remote, stream, ciphers, view);
            });
        }, source: source);
    }
}
