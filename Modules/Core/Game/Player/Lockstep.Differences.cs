using System.Diagnostics;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// Where serving lockstep differs from GGG's server, each on purpose and each with its reason. Lockstep.cs holds what
// follows GGG; 07 §6 lists these.
public static partial class Game
{
    // Differs from GGG: its server steps its world in frames, so a frame is a tick of the simulation. Ours walks on the
    // clock, so frames are laid over it: frame n ends n × FrameMs after the first, and a frame missed by a late timer goes
    // out at once instead of being lost, which keeps every frame start on the conn.Clock timeline the walks use (a free
    // 33 ms timer drifted 28 to 47 ms apart, with gaps to 390 ms). GGG's spacing is 30 to 36 ms. The Clock stops in the
    // Escape menu's pause, and so do the frames.
    // Differs from GGG: it never sends 0x2b8 (14 236 frames) and relies on the client's default, which is not traced; ours
    // says the length it paces by. Every connection runs these frames: the client alone picks its Network Mode, and a
    // predictive one ignores 0x2b7 (07 §6).
    public static async Task RunLockstepFrames(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        try
        {
            await Send(remote, stream, ciphers, InstanceProtocol.FrameLength(FrameMs), $"0x02b8 server frame length {FrameMs} ms");
            double frame = FrameMs / 1000d, first = conn.Clock.Elapsed.TotalSeconds;
            long ended = 0;
            while (!conn.Closed)
            {
                // Paused, the Clock stands and no frame falls due: it looks again a third of a frame later.
                if (conn.Paused)
                {
                    await Task.Delay(Math.Max(1, FrameMs / 3));
                    continue;
                }
                // Woken at the next frame end, on the Gateway's 1 ms timer (WindowsTimerResolution): a poll of a third of
                // a frame landed just before a boundary often enough to send frames 22 and 44 ms apart.
                double wait = first + (ended + 1) * frame - conn.Clock.Elapsed.TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(wait * 1000)));
                double now = conn.Clock.Elapsed.TotalSeconds;
                if (now < first + (ended + 1) * frame) continue;
                for (; first + (ended + 1) * frame <= now; ended++)
                {
                    // The frame advances in the frame end's send gate, under the combat lock: an order written after the
                    // 0x2b7 finds the new frame (LockstepOrderFrom), one written before it the old, and a pass that holds
                    // the lock sees one frame throughout.
                    long ending = ended;
                    await Send(remote, stream, ciphers, [], null, atGate: () =>
                    {
                        lock (conn.Monsters)
                        {
                            conn.ServerFrame++;
                            // A predictive client ignores the frames, so its timings keep the server's clock.
                            if (conn.LockstepClient) conn.LockstepFrame = (first + (ending + 1) * frame, first + (ending + 2) * frame);
                        }
                        return InstanceProtocol.FrameEnd();
                    });
                }
                await ProbeLockstepLatency(remote, stream, ciphers, conn);
                SendLockstepDefaultAttack(remote, stream, ciphers, conn);
                await StartLockstepActions(remote, stream, ciphers, conn, first + ended * frame);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    // Differs from GGG: it keys a Move from its tick by the player (u16 0x9001, 3 286 of 3 492 Moves). Keys must reach the
    // client sorted or the rest of the frame waits for its end (0x14218aff0), and everything else the Gateway sends is key
    // 0 in no set order, so ours is key 0 too: applied before the objects tick, which fits a walk starting at the frame's
    // start.
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
                lock (conn.Monsters)
                    if (!LockstepRepeats(conn, request.Skill) && conn.ChannelSkill == request.Skill && conn.ChannelSkillSequence == sequence)
                        request.Channelling(sequence);
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
