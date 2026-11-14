using System.Diagnostics;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// Where serving lockstep differs from GGG's server, each on purpose and each with its reason. Lockstep.cs holds what
// follows GGG; 07 §6 lists these.
public static partial class Game
{
    // The server frames themselves (their pacing on the clock, and 0x2b8), where they differ from GGG's, are the
    // framework's: Program.RunWorldFrames. What the rules do at each frame end is here.
    public static async Task LockstepFrameEnded(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double nextFrameStart)
    {
        await ProbeLockstepLatency(remote, stream, ciphers, conn);
        SendLockstepDefaultAttack(remote, stream, ciphers, conn);
        await StartLockstepActions(remote, stream, ciphers, conn, nextFrameStart);
    }

    // Differs from GGG: it keys a Move from its tick by the player (u16 0x9001, 3 286 of 3 492 Moves). Keys must reach the
    // client sorted or the rest of the frame waits for its end (0x14218aff0). The frame queue now enforces that order;
    // this producer retains key 0, applied before object ticks. Moving it into the player tick needs a separate
    // action-timing trace, since the server currently starts the walk at the frame start.
    static byte[] LockstepActionStart(ClientState conn, (int X, int Y) from, (int X, int Y) target, ushort sequence) =>
        InstanceProtocol.ActionStarted(PlayerId, from.X, from.Y, target.X, target.Y, conn.LockstepHold.Skill, sequence, conn.LockstepHold.Flags);

    // Differs from GGG: a monster out of its wake zone stands on its server as on the client, so nothing needs placing when
    // it comes back. Ours walks on out there, and a lockstep client skips 0x128 (case 0x50 of 0x142150ae0), so a returner
    // would stay on its old cell until its next action. 0x1b7 is not gated by the network mode.
    static byte[] LockstepPlace(List<(uint Id, int X, int Y)> back) =>
        [.. back.SelectMany(m => InstanceProtocol.Teleport(m.Id, m.X, m.Y))];

    // Differs from GGG: it reports the object's cell as the target. The client replaces the target with the object's own
    // position when it starts the action (0x14219aa10), and this path has no cell for every kind of object.
    static byte[] LockstepObjectStart((int X, int Y) from, InstanceTransfer.UseSkillOnObject use, ushort sequence) =>
        InstanceProtocol.ActionStarted(PlayerId, from.X, from.Y, from.X, from.Y, use.SkillId, sequence, use.Flags, targetObjectId: use.TargetId);

    // Differs from GGG: it starts a held skill from the player's tick, keyed by the player (u16 0x5001). Ours goes through
    // the handlers a predictive client's request reaches, with the server's sequence in place of the client's, so their
    // echo starts the action, in key 0 as every other send here is. Those handlers hold the skills' rules; a second copy
    // for lockstep would drift from them. Beside the frames, which must not wait for a cast.
    static void StartLockstepSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, ushort sequence, double at)
    {
        var request = conn.LockstepSkill;
        if (request.Target is { } target)
        {
            _ = OnUseObject(remote, stream, ciphers, conn, new(target, request.Skill, sequence, request.Flags), at);
            return;
        }
        if (LockstepGroundMelee(conn, request.Skill, request.Flags))
        {
            _ = StartLockstepGroundMelee(remote, stream, ciphers, conn, request.Aim, request.Skill, sequence, request.Flags);
            return;
        }
        byte[] use = new LoginWriter().U16(InstanceProtocol.UseSkillAtLocationId).U32((uint)request.Aim.X).U32((uint)request.Aim.Y)
            .U16(request.Skill).U16(sequence).U16(request.Flags).ToArray();
        _ = Task.Run(async () =>
        {
            try
            {
                await OnClientMessages(remote, stream, ciphers, [(InstanceProtocol.UseSkillAtLocationId, use)], [], conn);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });
    }

    // Differs from GGG: its server steps by frames and so can know the frame the client was on; a lockstep request carries
    // no frame here, so this estimates it. The client runs a round trip and about two frames behind the server (160 to
    // 220 ms at 100 ms, 66 to 100 ms on loopback), and its offsets are from the cell it had then. Called under conn.Monsters.
    public static (int X, int Y) LockstepClientCell(ClientState conn, double now)
    {
        double lowest = conn.LockstepLatency.Lowest;
        double when = now - ((double.IsNaN(lowest) ? 0 : lowest / 1000) + 2d * FrameMs / 1000);
        (int X, int Y)? cell = null;
        foreach (var (at, past) in conn.LockstepTrail)
        {
            if (at > when) break;
            cell = past;
        }
        return cell ?? conn.Walk.Position(now);
    }

    // Differs from GGG: 0x194 was in none of its sessions here, and the handler reads as an idle timeout (the client runs
    // /afk with its default text); here it times the client. 0x194 is the one message found that the client answers by
    // itself, so here it measures the real round trip (LockstepLatency) for LockstepClientCell, only to a client that has
    // shown it is in lockstep. It takes the wire alone, as
    // the 0x1c does, because a send gate held by game state would read as latency. The steps are random so that a probe
    // does not keep landing at the same point of a slow client's update.
    static async Task ProbeLockstepLatency(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (!conn.LockstepClient || now < conn.LockstepNextProbeAt) return;
        conn.LockstepNextProbeAt = now + (conn.LockstepLatency.Settled ? 2 : .07)
            + Random.Shared.NextDouble() * .05;
        long sent = Interlocked.Exchange(ref conn.LockstepProbeSentAt, 0);
        if (sent != 0 && Stopwatch.GetElapsedTime(sent).TotalMilliseconds < LockstepLatency.Lost)
        {
            Interlocked.CompareExchange(ref conn.LockstepProbeSentAt, sent, 0);
            return;
        }
        if (sent != 0) conn.LockstepLatency.Add(LockstepLatency.Lost);
        Interlocked.Exchange(ref conn.LockstepProbeSentAt, Stopwatch.GetTimestamp());
        await SendPong(stream, ciphers, InstanceProtocol.AfkRequest());
    }

    // The client's answer to 0x194: readAt is when its bytes reached the socket, so handlers running late do not count.
    public static void AnswerLockstepProbe(string remote, ClientState conn, long readAt)
    {
        long sent = Interlocked.Exchange(ref conn.LockstepProbeSentAt, 0);
        if (sent != 0) conn.LockstepLatency.Add(Stopwatch.GetElapsedTime(sent, readAt).TotalMilliseconds);
    }

    // Differs from GGG: a monster that gives up its pursuit (PlanMonsterAi, "gives up") gets no order and goes on with its
    // last one as the client's monster does, a chase's walk-up after the player (NextPlannedAction): the Gateway has no
    // idle AI. GGG sends nothing at that moment either, but idle orders follow: of 131 pursuits it ended with the player
    // 60+ cells off, 65 got a Move of 3+ cells, 30 a self-aimed Wait (the variety's granted Wait skill, flags 0x80), 9 a
    // flight and 15 an aimed order again after 3 s or more.

    // Not traced: a frozen monster (PlanMonsterAi, MonsterActionFactor 0) gets no stop on a lockstep client and walks on,
    // its estimate too, a chase's walk-up with its goal test (NextPlannedAction). The client takes no Move onto the
    // walker's own cell and does not stop the player for is_frozen (07 §5); what GGG sends to stop a frozen monster, if
    // anything, is not known.

    // Differs from GGG: a monster whose chase walk-up starts its swing (StepWalkUp, TakeLockstepChase) gets no 0x1ac
    // (MonsterSwing's begunAt path). GGG sends one when its own copy starts the swing; a lockstep client does nothing with
    // a 0x1ac (0x1416502e0), and its swing starts by its own goal test either way.
}
