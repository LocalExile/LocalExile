using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // How far around the player GGG lists monsters in a resync (the largest seen in captures is 23.4 cells).
    internal const int ResyncRadius = 24;
    // In a tight crowd a body's cell differs by one for a few milliseconds between client and server, which is enough to
    // send a monster round the other side, and nothing tells the server. The list only moves what is 20 or more cells off,
    // so sending it costs nothing when all is well; between stuns it is what ends such a drift.
    const double KeepInSyncSeconds = 1;
    const int KeepInSyncRadius = 64;

    // S→C 0x128 with the player first, then the monsters around it, where the server has them. Called under conn.Monsters.
    internal static async Task KeepInSync(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (now < conn.NextKeepInSyncAt || !conn.PlayerSent) return;
        conn.NextKeepInSyncAt = now + KeepInSyncSeconds;
        await Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
                return conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0 ? null
                    : ResyncPacketLocked(conn, conn.Clock.Elapsed.TotalSeconds, KeepInSyncRadius);
        });
    }

    internal static byte[] ResyncPacketLocked(ClientState conn, double now, int radius = ResyncRadius)
    {
        var player = conn.Walk.Position(now);
        List<(uint Id, int X, int Y)> objects = [(PlayerId, player.X, player.Y)];
        foreach (var (id, m) in conn.Monsters)
        {
            if (m.Dead || m.Life <= 0) continue;
            var at = m.Walk.Position(now);
            if (Dist(at, player) <= radius) objects.Add((id, at.X, at.Y));
        }
        return InstanceProtocol.Resync(objects);
    }

    /// <summary>How far a skill that travels a set distance along the aimed line goes, whatever the target's distance
    /// (live 2026-11-01; no stat carries it); 0 for one that goes to the aimed cell.</summary>
    static int TravelCells(string? effect) =>
        effect is null ? 0 : effect.StartsWith("WhirlingBlades", StringComparison.Ordinal) ? 40
            : effect is "Dash" or "DashHardMode" ? 33 : 0;

    /// <summary>Where a movement skill ends: the aimed cell, or the last walkable cell of the line to it. One that travels a
    /// set distance goes on past the target and stops at the first cell it cannot walk.</summary>
    internal static (int X, int Y) MovementLanding(ClientState conn, int effect, (int X, int Y) from, (int X, int Y) aim)
    {
        if (conn.Layout.Walkable is not { } walkable) return aim;
        if (TravelCells(SkillsFor(conn).EffectId(effect)) is > 0 and var cells && aim != from)
        {
            double f = cells / Math.Sqrt((double)(aim.X - from.X) * (aim.X - from.X) + (double)(aim.Y - from.Y) * (aim.Y - from.Y));
            var end = ((int)Math.Round(from.X + (aim.X - from.X) * f), (int)Math.Round(from.Y + (aim.Y - from.Y) * f));
            var last = from;
            foreach (var cell in ClientPathfinder.Line(from, end).Skip(1))
            {
                if (!walkable(cell.X, cell.Y)) break;
                last = cell;
            }
            return last;
        }
        foreach (var cell in ClientPathfinder.Line(aim, from))
            if (walkable(cell.X, cell.Y)) return cell;
        return from;
    }

    /// <summary>How long after a movement spell the resync goes out.</summary>
    internal const double MovementResyncSeconds = .5;

    internal static void ResyncAfter(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double seconds, string why) =>
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            try { await SendResync(remote, stream, ciphers, conn, why); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });

    internal static Task SendResync(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why) =>
        Send(remote, stream, ciphers, [], $"0x0128 resync ({why})", atGate: () =>
        {
            lock (conn.Monsters) return conn.Closed || conn.LeavingArea ? null : ResyncPacketLocked(conn, conn.Clock.Elapsed.TotalSeconds);
        });
}
