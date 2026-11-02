using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// Serving a lockstep client (07 §6), as GGG's server does it. Everything that differs from GGG on purpose is in
// Lockstep.Differences.cs, not here.
static partial class Game
{
    // C→S 0x18e, 0x18f, 0x192, 0x195 and 0x196 on a connection that serves lockstep. They only change what the player asks
    // for: everything starts in the frame pass, as on GGG's server, which answers a click at the next frame start and the
    // rest from the player's own tick. The offsets are from the client's cell; GGG's target is the cell it reports plus the
    // offset (94 of 95 clicks), so the server's cell is the one to use. The newest request wins. A request made during a
    // swing comes with flags 0x0001 and u8 3, and is served like any other.
    internal static void OnLockstepRequest(string remote, ClientState conn, ushort id, byte[] bytes)
    {
        lock (conn.Monsters)
        {
            conn.LockstepClient = true;
            var here = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            if (id == InstanceProtocol.LockstepMoveStartId)
            {
                var click = InstanceProtocol.ReadLockstepMoveStart(bytes);
                (int X, int Y) aim = (here.X + click.Dx, here.Y + click.Dy);
                conn.LockstepUse = null;
                if (SkillsFor(conn).ActionName(click.SkillId) == "Move")
                {
                    conn.LockstepSkill.Clear();
                    conn.LockstepHold.Press(aim, click.SkillId, click.Flags);
                }
                else
                {
                    Console.WriteLine($"[gateway] {remote} <= 0x018e skill 0x{click.SkillId:x4} at ({aim.X}, {aim.Y}), flags 0x{click.Flags:x4}, u8 {click.Extra}");
                    conn.LockstepHold.Clear();
                    conn.LockstepSkill.Press(aim, null, click.SkillId, click.Flags, LockstepRepeats(conn, click.SkillId));
                }
            }
            else if (id == InstanceProtocol.LockstepMoveUpdateId)
            {
                var (dx, dy) = InstanceProtocol.ReadLockstepMoveUpdate(bytes);
                conn.LockstepHold.Update((here.X + dx, here.Y + dy));
                conn.LockstepSkill.Update((here.X + dx, here.Y + dy));
            }
            else if (id == InstanceProtocol.LockstepMoveStopId)
            {
                conn.LockstepHold.Release();
                conn.LockstepSkill.Release();
            }
            else if (id == InstanceProtocol.LockstepSkillStopId)
            {
                if (InstanceProtocol.ReadLockstepSkillStop(bytes) == conn.LockstepSkill.Skill) conn.LockstepSkill.Release();
            }
            else if (id == InstanceProtocol.LockstepUseOnObjectId)
            {
                var (target, skill, flags, _) = InstanceProtocol.ReadLockstepUseOnObject(bytes);
                conn.LockstepHold.Clear();
                conn.LockstepSkill.Clear();
                if (SkillsFor(conn).IsGemSkill(skill) || skill == SkillCatalog.FirstSkillId)
                {
                    var aim = conn.Monsters.TryGetValue(target, out var m) ? m.Walk.Position(conn.Clock.Elapsed.TotalSeconds) : here;
                    conn.LockstepSkill.Press(aim, target, skill, flags, target != PlayerId && LockstepRepeats(conn, skill));
                }
                else conn.LockstepUse = new(target, skill, 0, flags);
            }
        }
    }

    // A channelled skill starts once and runs until the release; anything else repeats while the button stays down.
    static bool LockstepRepeats(ClientState conn, ushort skill) =>
        !(GemSkillEffects(conn).FirstOrDefault(g => g.Id == skill) is { Name: not null } gem && SkillsFor(conn).IsChannelled(gem.Effect));

    static ushort NextLockstepSequence(ClientState conn)
    {
        conn.LockstepSequence = (ushort)((conn.LockstepSequence + 1) & 0x7fff);
        return (ushort)(conn.LockstepSequence | 0x8000);
    }

    // The frame pass: starts what the player's requests call for, if anything, at the frame's start. Blocked steps are
    // replayed first, so a walk stopped by a body counts as stopped in this frame. Sequences go up by one with the top bit
    // set (5 058 of 5 058 on GGG's server). A lockstep client's 0x1ab always starts an action, and puts the player on the
    // reported cell whatever the distance (0x141e8e7e0), which is what keeps the two copies together.
    internal static async Task StartLockstepActions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double frameStart)
    {
        (int X, int Y) target = default, from = default;
        int action = 0;
        ushort sequence = 0, ended = 0;
        bool skill = false;
        double at;
        InstanceTransfer.UseSkillOnObject? use = null;
        using (ProjectileMutation(conn, phase: "lockstep"))
        {
            if (conn.Closed || conn.LeavingArea) return;
            // What a dead player asked for dies with it: a skill kept for the revival moved it off its checkpoint.
            if (conn.PlayerLife == 0)
            {
                conn.LockstepHold.Clear();
                conn.LockstepSkill.Clear();
                conn.LockstepUse = null;
                return;
            }
            at = Math.Max(frameStart, conn.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
            StepWalkers(conn, at);
            if (conn.LockstepSkill.ChannelReleased() is { } channel)
            {
                ended = channel;
                conn.ChannelSkill = 0;
                conn.ChannelGeneration++;
            }
            bool free = at >= conn.LockstepBusyUntil;
            if (!PlayerActionBlocked(conn, at, at) && free)
            {
                if (conn.LockstepSkill.Target is { } aimedAt && aimedAt != PlayerId
                    && !(conn.Monsters.TryGetValue(aimedAt, out var m) && !m.Dead && m.Life > 0))
                    conn.LockstepSkill.LoseTarget();
                if (conn.LockstepUse is { } object_)
                {
                    conn.LockstepUse = null;
                    use = object_;
                    from = conn.Walk.Position(at);
                    sequence = NextLockstepSequence(conn);
                }
                else if (conn.LockstepSkill.Due(free))
                {
                    skill = true;
                    sequence = NextLockstepSequence(conn);
                    conn.LockstepBusyUntil = at + LockstepActionSeconds(conn, conn.LockstepSkill.Skill);
                }
                else if (conn.LockstepHold.Due(conn.Walk.Moving(at), conn.Walk.Position(at)) is { } due)
                {
                    target = due;
                    action = Interlocked.Increment(ref conn.ActionCount);
                    from = conn.Walk.Order(target.X, target.Y, at);
                    conn.ChannelSkill = 0;
                    sequence = NextLockstepSequence(conn);
                }
            }
        }
        // GGG ends a channel with 0x1ae on the release (35 ms later).
        if (ended != 0) await Send(remote, stream, ciphers, InstanceProtocol.ChannelFinished(PlayerId, ended), $"0x01ae channel 0x{ended:x4} ended");
        if (sequence == 0) return;
        if (skill) StartLockstepSkill(remote, stream, ciphers, conn, sequence, at);
        else if (use is not null)
        {
            // The client walks up on this 0x1ab, where a predictive client starts by itself; the server's use goes on as
            // ever from here, its own walk included.
            await Send(remote, stream, ciphers, LockstepObjectStart(from, use, sequence), $"0x01ab lockstep use of object 0x{use.TargetId:x} from ({from.X}, {from.Y})");
            _ = OnUseObject(remote, stream, ciphers, conn, use with { Sequence = sequence }, at);
        }
        else await Send(remote, stream, ciphers, LockstepActionStart(conn, from, target, sequence),
            $"0x01ab lockstep walk from ({from.X}, {from.Y}) to ({target.X}, {target.Y})",
            still: () => PlayerActionCurrent(conn, action));
    }

    // C→S 0x192 on a channel: GGG answers with its new target (40 ms later). The channel's own hits run as ever.
    internal static Task AimLockstepChannel(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        (ushort Sequence, (int X, int Y) Aim)? channel;
        lock (conn.Monsters)
            channel = conn.LockstepSkill.Channel is { } sequence ? (sequence, conn.LockstepSkill.Aim) : null;
        return channel is { } c
            ? Send(remote, stream, ciphers, InstanceProtocol.ActionTarget(PlayerId, c.Sequence, c.Aim.X, c.Aim.Y), $"0x01ad channel aims at ({c.Aim.X}, {c.Aim.Y})")
            : Task.CompletedTask;
    }
}
