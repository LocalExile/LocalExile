using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// Walking on a lockstep client (07 §6), as GGG's server does it. Everything that differs from GGG on purpose is in
// Lockstep.Differences.cs, not here.
static partial class Game
{
    // C→S 0x18e, 0x192 and 0x195 on a connection that serves lockstep. They only change what the player asks for: the
    // walk itself starts in the frame pass, as on GGG's server, which answers a click at the next frame start and a held
    // target from the player's own tick. The offsets are from the client's cell; GGG's target is the cell it reports
    // plus the offset (94 of 95 clicks), so the server's cell is the one to use.
    internal static void OnLockstepRequest(string remote, ClientState conn, ushort id, byte[] bytes)
    {
        lock (conn.Monsters)
        {
            var here = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            if (id == InstanceProtocol.LockstepMoveStartId)
            {
                var click = InstanceProtocol.ReadLockstepMoveStart(bytes);
                if (SkillsFor(conn).ActionName(click.SkillId) == "Move")
                    conn.LockstepHold.Press((here.X + click.Dx, here.Y + click.Dy), click.SkillId, click.Flags);
                else NotServedInLockstep(remote, conn, id);
            }
            else if (id == InstanceProtocol.LockstepMoveUpdateId)
            {
                var (dx, dy) = InstanceProtocol.ReadLockstepMoveUpdate(bytes);
                conn.LockstepHold.Update((here.X + dx, here.Y + dy));
            }
            else if (id == InstanceProtocol.LockstepMoveStopId) conn.LockstepHold.Release();
            else NotServedInLockstep(remote, conn, id);
        }
    }

    // The frame pass: starts the Move the player's requests call for, if any, at the frame's start. Blocked steps are
    // replayed first, so a walk stopped by a body counts as stopped in this frame. Sequences go up by one with the top
    // bit set (5 058 of 5 058 on GGG's server). A lockstep client's 0x1ab always starts an action, and puts the player on
    // the reported cell whatever the distance (0x141e8e7e0), which is what keeps the two copies together.
    internal static async Task StartLockstepWalk(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double frameStart)
    {
        (int X, int Y) target, from;
        int action;
        ushort sequence;
        using (ProjectileMutation(conn, phase: "lockstep"))
        {
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
            double at = Math.Max(frameStart, conn.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
            StepWalkers(conn, at);
            if (PlayerActionBlocked(conn, at, at)) return;
            if (conn.LockstepHold.Due(conn.Walk.Moving(at), conn.Walk.Position(at)) is not { } due) return;
            target = due;
            action = Interlocked.Increment(ref conn.ActionCount);
            from = conn.Walk.Order(target.X, target.Y, at);
            conn.ChannelSkill = 0;
            conn.LockstepSequence = (ushort)((conn.LockstepSequence + 1) & 0x7fff);
            sequence = (ushort)(conn.LockstepSequence | 0x8000);
        }
        await Send(remote, stream, ciphers, LockstepActionStart(conn, from, target, sequence),
            $"0x01ab lockstep walk from ({from.X}, {from.Y}) to ({target.X}, {target.Y})",
            still: () => PlayerActionCurrent(conn, action));
    }
}
