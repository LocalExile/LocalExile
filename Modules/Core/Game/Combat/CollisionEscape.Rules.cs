using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The client's position is the truth: the server's walk only estimates it. A body the estimate stands in moves the
    // estimate out (its next contacts and blockers), never the player: a teleport there snapped the player back to where
    // the server guessed them. Only the arrival (tell: Messages.cs, the area entry, before the client has moved) places
    // the player itself.
    public static async Task PushOutOfCollision(Player player, bool living = false,
        bool tell = false)
    {
        if (!player.Connection.PlayerSent || player.PlayerLife == 0 || player.PendingTransfer is not null || player.Closed
            || player.Layout.GridW <= 0 || player.Layout.GridH <= 0) return;
        // A lockstep client never silently teleports out of stationary blockers; moving the server's walk without
        // telling the client causes the next 0x1ab Move to snap the client onto the escaped cell.
        if (!tell && player.Connection.LockstepClient) return;
        (int X, int Y) from, to;
        using (ProjectileMutation(player))
        {
            double now = ProjectileWorldTime(player);
            from = player.Walk.Position(now);
            // A wall that rose round the player (a closed door, risen spikes) leaves them where they stand: the client
            // stamps it under them and never moves its player out of it (its search, from a closed cell, steps only to
            // open neighbours; 0x141e50f90), so moving the estimate put a lockstep player elsewhere on its next order.
            if (player.Barrier.Contains(from) && player.Layout.Walkable?.Invoke(from.X, from.Y) != false) return;
            bool Walkable(int x, int y) => player.Layout.Walkable?.Invoke(x, y) != false && !player.Barrier.Contains((x, y));
            if (CollisionEscape.Find(from, player.Layout.GridW, player.Layout.GridH, Walkable,
                living ? player.CorrectionBlockers()
                    : player.StationaryBlockers) is not { } free) return;
            to = free;
            if (tell)
            {
                Interlocked.Increment(ref player.ActionCount);
                player.ChannelSkill = 0;
            }
            player.Walk.Teleport(to.X, to.Y, now);
        }
        if (!tell) return;
        await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), "stop action before collision correction");
        await SendWorldRecord(player, player.Id, InstanceProtocol.Teleport(player.Id, to.X, to.Y, 0x7fffffff),
            $"collision escape: ({from.X},{from.Y}) -> ({to.X},{to.Y})");
    }
}
