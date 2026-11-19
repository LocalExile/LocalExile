using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    /// <summary>
    /// Periodic work of one player on the area's timers (Righteous Fire's burn, the totems' casts): <paramref name="work"/>
    /// runs as the player every <paramref name="ms"/> while <paramref name="keep"/> holds, and the timer lapses once it does
    /// not (or the connection closed). A run still sending skips the next.
    /// </summary>
    static void EveryAsPlayer(ClientState conn, int ms, Func<bool> keep,
        Func<string, NetworkStream, ConnectionCiphers, ClientState, Task> work)
    {
        if (conn.Frames.Area is not { } area) return;
        int interval = Math.Max(1, (int)Math.Round(ms / (double)Program.FrameMs));
        Task pass = Task.CompletedTask;
        area.Defer(a => a.Timers.Every(a.Frame, interval, timer =>
        {
            if (conn.Closed || !keep()) { timer.Cancel(); return; }
            if (!pass.IsCompleted || !CurrentGameWork || conn.LeavingArea || conn.Paused
                || conn.PlayerUpdatesState is not PlayerUpdatesState updates || !PlayerUpdatesCurrent(conn, updates)) return;
            var (remote, stream, ciphers) = conn.Wire;
            pass = AsPlayer(conn, updates, () => work(remote, stream, ciphers, conn));
        }, source: CombatLifetime));
    }

    /// <summary>The seconds each run of <see cref="EveryAsPlayer"/> at <paramref name="ms"/> stands for.</summary>
    static double EverySeconds(int ms) => Math.Max(1, (int)Math.Round(ms / (double)Program.FrameMs)) * Program.FrameMs / 1000.0;
}
