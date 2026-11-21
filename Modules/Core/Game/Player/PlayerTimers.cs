using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    /// <summary>
    /// Periodic work of one player on the area's timers (Righteous Fire's burn, the totems' casts): <paramref name="work"/>
    /// runs as the player every <paramref name="ms"/> while <paramref name="keep"/> holds, and the timer lapses once it does
    /// not (or the connection closed). A run still sending skips the next.
    /// </summary>
    static void EveryAsPlayer(Player player, int ms, Func<bool> keep,
        Func<Player, Task> work)
    {
        int interval = Math.Max(1, (int)Math.Round(ms / (double)Program.FrameMs));
        Task pass = Task.CompletedTask;
        InArea(player, a => a.Timers.Every(a.Frame, interval, timer =>
        {
            if (player.Closed || !keep()) { timer.Cancel(); return; }
            if (!pass.IsCompleted || !CurrentGameWork || player.LeavingArea || player.Paused
                || player.PlayerUpdatesState is not PlayerUpdatesState updates || !PlayerUpdatesCurrent(player, updates)) return;
            pass = AsPlayer(player, updates, () => work(player));
        }, source: CombatLifetime));
    }

    /// <summary>The player's work of every frame of the area (the fixed update): <paramref name="work"/> runs as the player
    /// each frame while <paramref name="keep"/> holds. The work reads the elapsed game time itself.</summary>
    static void EveryFrameAsPlayer(Player player, Func<bool> keep,
        Func<Player, Task> work) => EveryAsPlayer(player, Program.FrameMs, keep, work);

    /// <summary>One frame of the area, in seconds.</summary>
    static double FrameSeconds => Program.FrameMs / 1000.0;

    /// <summary>Runs <paramref name="work"/> once on the area's timers in <paramref name="seconds"/> (an effect's end).</summary>
    static void AfterSeconds(Player player, double seconds, Action work)
    {
        long frames = Math.Max(1, (long)Math.Ceiling(Math.Max(0, seconds) * 1000 / Program.FrameMs));
        InArea(player, a => a.Timers.After(a.Frame, frames, _ => { if (!player.Closed && CurrentGameWork) work(); }, source: CombatLifetime));
    }

    /// <summary>Runs <paramref name="work"/> once as the player on the area's timers in <paramref name="seconds"/>, while
    /// the player's updates still run.</summary>
    static void AfterAsPlayer(Player player, double seconds, Func<Player, Task> work) =>
        AfterSeconds(player, seconds, () =>
        {
            if (player.LeavingArea || player.PlayerUpdatesState is not PlayerUpdatesState updates || !PlayerUpdatesCurrent(player, updates)) return;
            _ = AsPlayer(player, updates, () => work(player));
        });

    // In the area's frame, once its world exists (a player's updates start before the frames' first member joined).
    static void InArea(Player player, Action<GameArea> work)
    {
        if (player.Frames.Area is { } area) area.Defer(work);
        else _ = player.Frames.AreaReady.ContinueWith(t => t.Result.Defer(work),
            TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>The seconds each run of <see cref="EveryAsPlayer"/> at <paramref name="ms"/> stands for.</summary>
    static double EverySeconds(int ms) => Math.Max(1, (int)Math.Round(ms / (double)Program.FrameMs)) * Program.FrameMs / 1000.0;
}
