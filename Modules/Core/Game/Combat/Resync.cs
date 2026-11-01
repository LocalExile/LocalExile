using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // How far around the player GGG lists monsters in a resync (the largest seen in captures is 23.4 cells).
    internal const int ResyncRadius = 24;

    // S→C 0x128 with the player first, then the monsters around it, where the server has them. Called under conn.Monsters.
    internal static byte[] ResyncPacketLocked(ClientState conn, double now)
    {
        var player = conn.Walk.Position(now);
        List<(uint Id, int X, int Y)> objects = [(PlayerId, player.X, player.Y)];
        foreach (var (id, m) in conn.Monsters)
        {
            if (m.Dead || m.Life <= 0) continue;
            var at = m.Walk.Position(now);
            if (Dist(at, player) <= ResyncRadius) objects.Add((id, at.X, at.Y));
        }
        return InstanceProtocol.Resync(objects);
    }

    /// <summary>How far Whirling Blades travels along the aimed line (40 cells whatever the target's distance, live
    /// 2026-11-01); no stat carries it.</summary>
    const int WhirlingBladesCells = 40;

    /// <summary>Where a movement skill ends: the aimed cell, or the last walkable cell of the line to it. Whirling Blades
    /// goes on past the target and stops at the first cell it cannot walk.</summary>
    internal static (int X, int Y) MovementLanding(ClientState conn, int effect, (int X, int Y) from, (int X, int Y) aim)
    {
        if (conn.Layout.Walkable is not { } walkable) return aim;
        if (SkillsFor(conn).EffectId(effect)?.StartsWith("WhirlingBlades", StringComparison.Ordinal) == true && aim != from)
        {
            double f = WhirlingBladesCells / Math.Sqrt((double)(aim.X - from.X) * (aim.X - from.X) + (double)(aim.Y - from.Y) * (aim.Y - from.Y));
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
