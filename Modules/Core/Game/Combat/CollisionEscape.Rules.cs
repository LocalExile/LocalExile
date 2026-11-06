using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // The client's position is the truth: the server's walk only estimates it. A body the estimate stands in moves the
    // estimate out (its next contacts and blockers), never the player: a teleport there snapped the player back to where
    // the server guessed them. Only the arrival (tell: Messages.cs, the area entry, before the client has moved) places
    // the player itself.
    internal static async Task PushOutOfCollision(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool living = false,
        bool tell = false)
    {
        if (!conn.PlayerSent || conn.PlayerLife == 0 || conn.PendingTransfer is not null || conn.Closed
            || conn.Layout.GridW <= 0 || conn.Layout.GridH <= 0) return;
        (int X, int Y) from, to;
        using (ProjectileMutation(conn))
        {
            double now = ProjectileWorldTime(conn);
            from = conn.Walk.Position(now);
            bool Walkable(int x, int y) => conn.Layout.Walkable?.Invoke(x, y) != false && !conn.Barrier.Contains((x, y));
            if (CollisionEscape.Find(from, conn.Layout.GridW, conn.Layout.GridH, Walkable,
                living ? conn.CorrectionBlockers()
                    : conn.StationaryBlockers) is not { } free) return;
            to = free;
            if (tell)
            {
                Interlocked.Increment(ref conn.ActionCount);
                conn.ChannelSkill = 0;
            }
            conn.Walk.Teleport(to.X, to.Y, now);
        }
        if (!tell) return;
        await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(PlayerId), "stop action before collision correction");
        await Send(remote, stream, ciphers, InstanceProtocol.Teleport(PlayerId, to.X, to.Y, 0x7fffffff),
            $"collision escape: ({from.X},{from.Y}) -> ({to.X},{to.Y})");
    }
}
