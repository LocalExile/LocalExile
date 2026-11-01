using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
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

    internal static Task SendResync(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why) =>
        Send(remote, stream, ciphers, [], $"0x0128 resync ({why})", atGate: () =>
        {
            lock (conn.Monsters) return conn.Closed || conn.LeavingArea ? null : ResyncPacketLocked(conn, conn.Clock.Elapsed.TotalSeconds);
        });
}
