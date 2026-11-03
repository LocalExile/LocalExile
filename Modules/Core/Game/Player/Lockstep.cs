using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// Serving a lockstep client (07 §6), as GGG's server does it. Everything that differs from GGG on purpose is in
// Lockstep.Differences.cs, not here.
static partial class Game
{
    // C→S 0x18e, 0x18f, 0x192, 0x195 and 0x196 on a connection that serves lockstep. They only change what the player asks
    // for: everything starts in the frame pass, as on GGG's server, which answers a click at the next frame start and the
    // rest from the player's own tick. The offsets are from the client's cell, and GGG's target is that cell plus the
    // offset: where the cursor is (median 0.8 cells off over 5 300 requests, moving or not). The newest request wins. A request made during a
    // swing comes with flags 0x0001 and u8 3, and is served like any other.
    internal static void OnLockstepRequest(string remote, ClientState conn, ushort id, byte[] bytes)
    {
        lock (conn.Monsters)
        {
            conn.LockstepClient = true;
            var here = LockstepClientCell(conn, conn.Clock.Elapsed.TotalSeconds);
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
            // Requests wait here until the round trip is known; on a refused connection none arrive.
            if (!conn.LockstepLatency.Confirmed) return;
            at = Math.Max(frameStart, conn.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
            StepWalkers(conn, at);
            conn.LockstepTrail.Enqueue((at, conn.Walk.Position(at)));
            if (conn.LockstepTrail.Count > 150) conn.LockstepTrail.Dequeue();
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
                // A held skill repeats once per action, as GGG's does: an attack's next use waits for the attack time of
                // the one before, aimed at a monster (its walk-up and swing: LockstepAttackStart) or at the ground (live
                // 2026-11-03: a ground attack sent as soon as the player was free came 0.4 s into a 0.645 s swing, and
                // the client queued it until the swing's end).
                bool monster = conn.LockstepSkill.Target is { } t && t != PlayerId;
                bool attack = LockstepAttackSkill(conn, conn.LockstepSkill.Skill);
                if (conn.LockstepUse is { } object_)
                {
                    conn.LockstepUse = null;
                    use = object_;
                    from = conn.Walk.Position(at);
                    sequence = NextLockstepSequence(conn);
                }
                else if (conn.LockstepAttackAction == 0 && (!attack || at + 1e-9 >= conn.NextAttackAt) && conn.LockstepSkill.Due(free))
                {
                    skill = true;
                    sequence = NextLockstepSequence(conn);
                    // A use at the ground starts at the end of this frame on the client, and holds the player from there.
                    if (!monster) conn.LockstepBusyUntil = (conn.LockstepFrame?.End ?? at) + LockstepActionHold(conn, conn.LockstepSkill.Skill);
                }
                else if (conn.LockstepHold.Due(conn.Walk.Moving(at), conn.Walk.Position(at)) is { } due)
                {
                    target = due;
                    action = Interlocked.Increment(ref conn.ActionCount);
                    // A Move whose target is within two cells of the player's cell ends on the client as it starts: no
                    // path, no step and no move onto the pair (live 2026-11-03: 25 of 25 such starts, where every start
                    // 3 or more cells away walked). The estimate stands where it is, as the client does.
                    var here = conn.Walk.Position(at);
                    bool near = Math.Max(Math.Abs(target.X - here.X), Math.Abs(target.Y - here.Y)) <= 2;
                    from = near ? conn.Walk.Order(here.X, here.Y, at) : conn.Walk.Order(target.X, target.Y, at);
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

    // A skill asked on a monster: GGG answers the request with the action's start in the next frame, from the player's
    // cell and aimed at the monster (0x18f gets its 0x1ab with flag 0x80 and the target 36 to 54 ms later), and the client's
    // own action walks up and swings when it gets there, as a monster's chase does; a held skill then repeats once per
    // action. A predictive client starts the walk-up itself and only hears the swing. So on a lockstep client the start
    // goes out now, the server's walk-up and swing run on without a second 0x1ab, and the swing's time is when the
    // estimate gets there (LockstepAttackSwings). False when the client is not in lockstep: the swing says it, as before.
    internal static async Task<bool> LockstepAttackStart(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, (int X, int Y) here, (int X, int Y) target, int action, string what)
    {
        if (!conn.LockstepClient) return false;
        lock (conn.Monsters) conn.LockstepAttackAction = action;
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
            $"0x01ab lockstep {what} 0x{use.SkillId:x4} on 0x{use.TargetId:x} from ({here.X}, {here.Y}) toward ({target.X}, {target.Y})",
            still: () => PlayerActionCurrent(conn, action));
        return true;
    }

    // Whether a held skill is an attack (the default attack or an attack gem): its uses share the attack timer.
    static bool LockstepAttackSkill(ClientState conn, ushort skill) =>
        skill == SkillCatalog.FirstSkillId
        || GemSkillEffects(conn).FirstOrDefault(g => g.Id == skill) is { Name: not null } gem && SkillsFor(conn).IsAttack(gem.Effect);

    // How long a use started at the ground or on the player holds it: an attack until the client lets a Move cut in
    // (LockstepAttackCutIn), a spell for its cast time (a Move during a cast is not measured), a channel not at all (it
    // runs until the release).
    static double LockstepActionHold(ClientState conn, ushort skill)
    {
        if (GemSkillEffects(conn).FirstOrDefault(g => g.Id == skill) is { Name: not null } gem)
        {
            if (SkillsFor(conn).IsChannelled(gem.Effect)) return 0;
            if (!SkillsFor(conn).IsAttack(gem.Effect)) return SkillsFor(conn).CastTimeMs(gem.Effect) / 1000d / PlayerActionFactor(conn);
        }
        return LockstepAttackCutIn(conn, skill) + conn.LockstepActionTail;
    }

    // The attack time of a held attack skill, as its handler paces it (Attack, CastGemSkill).
    static double LockstepAttackSeconds(ClientState conn, ushort skill) => skill == SkillCatalog.FirstSkillId
        ? Math.Max(TickSeconds, WeaponFor(conn).AttackSeconds(CharacterPlayerStats(conn)) / PlayerActionFactor(conn))
        : SupportedAttackSeconds(conn, skill);

    // When, after a swing's start, the client lets a Move cut the attack short: its clip's lock_cancelling keyframe where
    // it has one, else its contact (live 2026-11-03, the first Move the client ran at once: a wand's 0.334 s into a
    // 0.667 s attack at a 0.300 s contact, a one-hand sword's 0.58 s into a 0.645 s attack at a 0.56 s contact, a bow's
    // 0.40 s into 0.714 s at its lock_cancelling, 600 of 1066 ms, with the contact at 0.27). A Move sent before that is
    // queued on the client until then (0x141e8cf10 takes it, the actor runs it at the keyframe), while the server's
    // estimate would walk alone. The default attack's keyframes are its stance clip's (DefaultAttackClip.CutInSeconds);
    // an attack gem's cut-in is taken as the attack1 contact's fraction of the attack (AttackContactDelay).
    internal static double LockstepAttackCutIn(ClientState conn, ushort skill, ushort sequence = 0)
    {
        double seconds = LockstepAttackSeconds(conn, skill);
        if (skill != SkillCatalog.FirstSkillId) return AttackContactDelay(conn, seconds, approach: false);
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject;
        return Animations.DefaultAttackCutInSeconds(ao, WeaponProfileFor(conn, activeSet: true), sequence, seconds);
    }

    // The walk-up of a lockstep client's attack is over and the swing begins at now: it holds the player for
    // <paramref name="seconds"/> (until the client lets a Move cut in, LockstepAttackCutIn), and the held skill's next use
    // may follow it. Called under conn.Monsters.
    internal static void LockstepAttackSwings(ClientState conn, int action, double now, double seconds)
    {
        if (conn.LockstepAttackAction != action) return;
        conn.LockstepAttackAction = 0;
        // The client starts the action at the end of the frame its 0x1ab goes out in, so the hold counts from there
        // (live: a wand's first Move 0.333 s after the client's start was refused at a 0.3335 s contact, 0.334 s taken).
        conn.LockstepBusyUntil = Math.Max(conn.LockstepBusyUntil, (conn.LockstepFrame?.End ?? now) + seconds);
    }

    // The attack ended before its swing (cancelled, out of reach): nothing holds the next use.
    internal static void LockstepAttackOver(ClientState conn, int action)
    {
        lock (conn.Monsters) if (conn.LockstepAttackAction == action) conn.LockstepAttackAction = 0;
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
