using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// Serving a lockstep client (07 §6), as GGG's server does it. Everything that differs from GGG on purpose is in
// Lockstep.Differences.cs, not here.
static partial class Game
{
    // C→S 0x18e, 0x18f, 0x192, 0x195 and 0x196, which a client sends only in lockstep. They only change what the player asks
    // for: everything starts in the frame pass, as on GGG's server, which answers a click at the next frame start and the
    // rest from the player's own tick. The offsets are from the client's cell, and GGG's target is that cell plus the
    // offset: where the cursor is (median 0.8 cells off over 5 300 requests, moving or not). The newest request wins. A request made during a
    // swing comes with flags 0x0001 and u8 3, and is served like any other.
    internal static void OnLockstepRequest(string remote, ClientState conn, ushort id, byte[] bytes)
    {
        lock (conn.Monsters)
        {
            conn.NoteLockstepClient(remote);
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
    // reported cell whatever the distance (0x141e95080), which is what keeps the two copies together.
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
            // A predictive client keeps its own walk: the frame pass is a lockstep client's only.
            if (!conn.LockstepClient) return;
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
            // Readiness belongs to the client's frame. Projectile time may already be ahead of it; using that
            // clock can start a Move before pickup completion, whose cleanup then discards the new path.
            bool free = frameStart >= conn.LockstepBusyUntil;
            if (!PlayerActionBlocked(conn, at, at) && free)
            {
                if (conn.LockstepSkill.Target is { } aimedAt && aimedAt != PlayerId
                    && !(conn.Monsters.TryGetValue(aimedAt, out var m) && !m.Dead && m.Life > 0))
                    conn.LockstepSkill.LoseTarget();
                // A held skill repeats once per action, as GGG's does: an attack's next use waits for the attack time of
                // the one before, aimed at a monster (its walk-up and swing: LockstepAttackStart) or at the ground (live
                // 2026-11-03: a ground attack sent as soon as the player was free came 0.4 s into a 0.645 s swing, and
                // the client queued it until the swing's end). A new click goes at the next frame start over an open
                // walk-up, as over a walk, and its action ends that walk-up (PlayerWalkUpOpen); only a swing holds it.
                bool monster = conn.LockstepSkill.Target is { } t && t != PlayerId;
                bool attack = LockstepAttackSkill(conn, conn.LockstepSkill.Skill);
                if (conn.LockstepUse is { } object_)
                {
                    conn.LockstepUse = null;
                    use = object_;
                    from = conn.Walk.Position(at);
                    sequence = NextLockstepSequence(conn);
                }
                else if ((!attack || at + 1e-9 >= conn.NextAttackAt)
                    && conn.LockstepSkill.Due(free, repeat: conn.LockstepAttack is null))
                {
                    skill = true;
                    sequence = NextLockstepSequence(conn);
                    // A use at the ground starts at the end of this frame on the client, and holds the player from there.
                    if (!monster && !LockstepGroundMelee(conn, conn.LockstepSkill.Skill, conn.LockstepSkill.Flags))
                        conn.LockstepBusyUntil = (conn.LockstepFrame?.End ?? at) + LockstepActionHold(conn, conn.LockstepSkill.Skill, sequence);
                }
                else if (conn.LockstepHold.Due(conn.Walk.Moving(at), conn.Walk.Position(at)) is { } due
                    && PlaceLockstepPlayerMove(conn, due, at) is { } placed)
                {
                    (target, from) = (due, placed);
                    action = Interlocked.Increment(ref conn.ActionCount);
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
            _ = OnUseObject(remote, stream, ciphers, conn, use with { Sequence = sequence, Started = true }, at);
        }
        else await Send(remote, stream, ciphers, LockstepActionStart(conn, from, target, sequence),
            $"0x01ab lockstep walk from ({from.X}, {from.Y}) to ({target.X}, {target.Y})",
            still: () => PlayerActionCurrent(conn, action));
    }

    // A player Move due at `at`: placed and its start cell returned when the client takes it (LockstepTakesMove). The
    // client admits a Move by vtable slot +0x130 (0x141949a20): dx*dx + dy*dy >= 9 from the walker's own cell, which
    // 0x141e94900 runs before it ends the current action and before 0x141e95080 sets the pair. A refused Move is
    // destroyed there: no position set, no search, and the action the player is in goes on (live 2026-11-04, the
    // player's 0x1ab applied under trace-lockstep-tick.js with the path search hooked: 308 of 308 Moves within two
    // cells untouched, 127 of them with a from cell other than the client's; every farther one searched). So none is
    // sent and nothing is spent, the estimate's walk goes on as the client's does, and a held cursor stays due
    // (LockstepHold: it is offered again once the walk stops short of it).
    internal static (int X, int Y)? PlaceLockstepPlayerMove(ClientState conn, (int X, int Y) target, double at)
    {
        var here = conn.Walk.Position(at);
        return LockstepTakesMove(here, target) ? conn.Walk.Order(target.X, target.Y, at) : null;
    }

    // A skill asked on a monster: GGG answers the request with the action's start in the next frame, from the player's
    // cell and aimed at the monster (0x18f gets its 0x1ab with flag 0x80 and the target 36 to 54 ms later), and the client's
    // own action walks up and swings when it gets there, as a monster's chase does; a held skill then repeats once per
    // action. A predictive client starts the walk-up itself and only hears the swing. So on a lockstep client the start
    // goes out now, and its walk-up is the client's: placed where the 0x1ab goes out (LockstepAttackAtGate) and run by the
    // step pass (StepFrames), which ends it in the swing the client's own goal test starts (LockstepAttack.Decided), with
    // no second 0x1ab. Null when the client is not in lockstep: the swing says it, as before.
    internal static async Task<LockstepAttack?> LockstepAttackStart(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceTransfer.UseSkillOnObject use, SpawnedMonster m, SkillCatalog.Unarmed weapon, int precision,
        int action, string what)
    {
        if (!conn.LockstepClient) return null;
        LockstepAttack attack;
        lock (conn.Monsters)
        {
            conn.LockstepAttack?.Fails();
            conn.LockstepAttack = attack = new(action, use.TargetId, m, WalkUpRange(weapon), m.StopRange - 2, precision,
                () => LockstepAttackCutIn(conn, use.SkillId, use.Sequence) + conn.LockstepActionTail);
        }
        await Send(remote, stream, ciphers, [], $"0x01ab lockstep {what} 0x{use.SkillId:x4} on 0x{use.TargetId:x}",
            atGate: () =>
            {
                using (ProjectileMutation(conn, phase: "send")) return LockstepAttackAtGate(remote, conn, attack, use);
            });
        // Never placed (the gate dropped it): nothing walks up. Decided already, this changes nothing.
        lock (conn.Monsters) if (!attack.Open) attack.Fails();
        return attack;
    }

    // The attack's 0x1ab at its send gate. The client applies it at the start of the frame being sent, so its walk-up is
    // placed there, after the steps the walk the client still runs takes before it (StepWalkers), from the cell they leave
    // the player on, as a monster's order is (LockstepOrderFrom; 1 446 of 1 447 traced setups of the player's actions came
    // in the frame of an order's apply). Null, and the attack failed, when another action replaced it. Under conn.Monsters.
    internal static byte[]? LockstepAttackAtGate(string remote, ClientState conn, LockstepAttack attack, InstanceTransfer.UseSkillOnObject use)
    {
        if (conn.LockstepAttack != attack || !PlayerActionCurrent(conn, attack.Action))
        {
            attack.Fails();
            return null;
        }
        double at = conn.LockstepFrame?.Start ?? ProjectileWorldTime(conn);
        StepWalkers(conn, at);
        var (here, aim) = PlaceLockstepAttack(conn, attack, at);
        string walkUp = attack.Open ? $"walks up to ({conn.Walk.Destination.X}, {conn.Walk.Destination.Y})"
            : attack.Decided.Result is null ? "no path" : "swings at once";
        Console.WriteLine($"[gateway] {remote} lockstep skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} on 0x{use.TargetId:x} at {at:0.000}: " +
            $"player ({here.X}, {here.Y}), monster ({aim.X}, {aim.Y}), distance {Dist(here, aim):0.0}; {walkUp}");
        return InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, aim.X, aim.Y, use.SkillId, use.Sequence, use.Flags,
            targetObjectId: use.TargetId);
    }

    // The attack's MoveTo placed at a lockstep frame's start `at`, toward the monster's own cell (0x14123f9c0), which the
    // apply reads before any object ticks. MoveTo tests its goal where the player stands first (0x141e81c20 →
    // 0x141e81950): met, the swing begins in this frame, from where the frame's steps leave a walking player
    // (0x141e82310 → 0x141e38420 tests that same cell, then stops after the steps, even when they carry him out of reach);
    // nothing found ends the action (0x141e82010 → 0x141e82310); else the step pass runs the walk-up. Under conn.Monsters.
    internal static ((int X, int Y) Here, (int X, int Y) Aim) PlaceLockstepAttack(ClientState conn, LockstepAttack attack, double at)
    {
        var aim = attack.AimAt(at);
        var here = conn.Walk.Approach(aim.X, aim.Y, attack.Reach, at, attack.Tolerance, attack.Precision).Here;
        attack.TimerFrom = at;
        if (conn.Walk.InReachOf(here, aim, attack.Reach, attack.Tolerance))
            LockstepAttackSwings(conn, attack, at, conn.LockstepFrame?.End ?? at);
        else if (conn.Walk.Reached && !conn.Walk.ReachedGoal) attack.Fails();
        else attack.Open = true;
        return (here, aim);
    }

    internal static bool LockstepGroundMelee(ClientState conn, ushort skill, ushort flags) =>
        skill == SkillCatalog.FirstSkillId && (flags & 1) == 0 && WeaponProfileFor(conn, activeSet: true).Melee;

    // Actor starts a ground melee use with MoveTo too (0x141e95400 -> 0x141e81c20): weapon range minus one,
    // tolerance -1, precision 1. Only flag 1 forces a swing in place. Its fixed aim shares the monster walk-up's
    // frame stepping and cancellation; the attack timer starts when the walk-up reaches its goal.
    internal static async Task StartLockstepGroundMelee(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, (int X, int Y) aim, ushort skill, ushort sequence, ushort flags)
    {
        if (InTown(conn)) return;
        LockstepAttack attack;
        lock (conn.Monsters)
        {
            double now = ProjectileWorldTime(conn);
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || PlayerActionBlocked(conn, now, now)) return;
            int action = Interlocked.Increment(ref conn.ActionCount);
            conn.ChannelSkill = 0;
            conn.LockstepAttack?.Fails();
            conn.LockstepAttack = attack = new(action, 0, null, WalkUpRange(WeaponFor(conn)), -1, 1,
                () => LockstepAttackCutIn(conn, skill, sequence) + conn.LockstepActionTail)
            {
                GroundTarget = aim,
                GroundAttackSeconds = () => LockstepAttackSeconds(conn, skill),
            };
        }
        try
        {
            await EndGracePeriod(remote, stream, ciphers, conn, action: true);
            await Send(remote, stream, ciphers, [], $"0x01ab lockstep ground attack 0x{skill:x4}", atGate: () =>
            {
                using (ProjectileMutation(conn, phase: "send"))
                {
                    if (conn.LockstepAttack != attack || !PlayerActionCurrent(conn, attack.Action)) return null;
                    double at = conn.LockstepFrame?.Start ?? ProjectileWorldTime(conn);
                    StepWalkers(conn, at);
                    var placed = PlaceLockstepAttack(conn, attack, at);
                    return InstanceProtocol.ActionStarted(PlayerId, placed.Here.X, placed.Here.Y, aim.X, aim.Y,
                        skill, sequence, flags);
                }
            });
            lock (conn.Monsters) if (!attack.Open) attack.Fails();
            await attack.Decided;
        }
        finally { LockstepAttackOver(conn, attack.Action); }
    }

    // Whether a held skill is an attack (the default attack or an attack gem): its uses share the attack timer.
    static bool LockstepAttackSkill(ClientState conn, ushort skill) =>
        skill == SkillCatalog.FirstSkillId
        || GemSkillEffects(conn).FirstOrDefault(g => g.Id == skill) is { Name: not null } gem && SkillsFor(conn).IsAttack(gem.Effect);

    // How long a use started at the ground or on the player holds it: an attack until the client lets a Move cut in
    // (LockstepAttackCutIn), a spell for its cast time (a Move during a cast is not measured), a channel not at all (it
    // runs until the release).
    static double LockstepActionHold(ClientState conn, ushort skill, ushort sequence)
    {
        if (GemSkillEffects(conn).FirstOrDefault(g => g.Id == skill) is { Name: not null } gem)
        {
            if (SkillsFor(conn).IsChannelled(gem.Effect)) return 0;
            if (!SkillsFor(conn).IsAttack(gem.Effect)) return SkillsFor(conn).CastTimeMs(gem.Effect) / 1000d / PlayerActionFactor(conn);
        }
        return LockstepAttackCutIn(conn, skill, sequence) + conn.LockstepActionTail;
    }

    // The attack time of a held attack skill, as its handler paces it (Attack, CastGemSkill).
    static double LockstepAttackSeconds(ClientState conn, ushort skill) => skill == SkillCatalog.FirstSkillId
        ? Math.Max(TickSeconds, WeaponFor(conn).AttackSeconds(CharacterPlayerStats(conn)) / PlayerActionFactor(conn))
        : SupportedAttackSeconds(conn, skill);

    // When, after a swing's start, the client lets a Move cut the attack short: its clip's lock_cancelling keyframe where
    // it has one, else its contact (live 2026-11-03, the first Move the client ran at once: a wand's 0.334 s into a
    // 0.667 s attack at a 0.300 s contact, a one-hand sword's 0.58 s into a 0.645 s attack at a 0.56 s contact, a bow's
    // 0.40 s into 0.714 s at its lock_cancelling, 600 of 1066 ms, with the contact at 0.27). A Move sent before that is
    // queued on the client until then (0x141e937b0 takes it, the actor runs it at the keyframe), while the server's
    // estimate would walk alone. The default attack's keyframes are its stance clip's (DefaultAttackClip.CutInSeconds);
    // an attack gem's cut-in is taken as the attack1 contact's fraction of the attack (AttackContactDelay).
    internal static double LockstepAttackCutIn(ClientState conn, ushort skill, ushort sequence = 0)
    {
        double seconds = LockstepAttackSeconds(conn, skill);
        if (skill != SkillCatalog.FirstSkillId) return AttackContactDelay(conn, seconds, approach: false);
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject;
        return Animations.DefaultAttackCutInSeconds(ao, WeaponProfileFor(conn, activeSet: true), sequence, seconds);
    }

    // The walk-up is over: the swing began in the frame [start, end] (the order's own, or the step pass's goal frame), from
    // where the stop leaves the player. It holds him for the attack's Hold (until the client lets a Move cut in,
    // LockstepAttackCutIn) from that frame's end (live: a wand's first Move 0.333 s after the client's start was refused
    // at a 0.3335 s contact, 0.334 s taken), set here so a held Move in the same frame pass waits for it. Its attack time
    // is the one the swing starts with, as the actor's animation takes it then (0x141e9c700, a monster's +0x26c), so a
    // chill or a buff gone during the walk-up counts. Not traced for a walk-up's swing: no timer of the player's own
    // swing was found (Actor +0x26c stays 0), so it is dated as a monster's walk-up swing is (its +0x26c one frame down
    // at its frame's end, 48 of 48). Under conn.Monsters.
    internal static void LockstepAttackSwings(ClientState conn, LockstepAttack attack, double start, double end)
    {
        conn.LockstepBusyUntil = Math.Max(conn.LockstepBusyUntil, end + attack.Hold());
        if (attack.GroundAttackSeconds is { } seconds)
            conn.NextAttackAt = AttackTiming.After(start, conn.NextAttackAt, seconds());
        attack.Swings(start, end, conn.Walk.Destination);
    }

    // The attack is over, swung or not: nothing more holds the held skill's next use.
    internal static void LockstepAttackOver(ClientState conn, int action)
    {
        lock (conn.Monsters)
            if (conn.LockstepAttack is { } attack && attack.Action == action)
            {
                attack.Fails();
                conn.LockstepAttack = null;
            }
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
