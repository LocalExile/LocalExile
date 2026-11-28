using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// Serving a lockstep client (07 §6), as GGG's server does it. Everything that differs from GGG on purpose is in
// Lockstep.Differences.cs, not here.
public static partial class Game
{
    const double MovingChannelAimInterval = .05;

    static bool HeldLockstepAimedChannel(Player player) =>
        player.Connection.LockstepClient && player.LockstepSkill.Held
            && player.LockstepSkill.Channel == player.ChannelSkillSequence
            && (player.ChannelMovement.Skill != 0 && player.ChannelMovement.Skill == player.ChannelSkill
                || CurrentChargedDash(player) is not null
                || ActiveWitherChannel(player) is not null
                || ActiveAimedPulseChannel(player) is not null
                || IsBladeFlurryChannelling(player, player.ChannelSkill, player.ChannelGeneration, player.ActionCount));

    // C→S 0x18e, 0x18f, 0x192, 0x195 and 0x196, which a client sends only in lockstep. They only change what the player asks
    // for: everything starts in the frame pass, as on GGG's server, which answers a click at the next frame start and the
    // rest from the player's own tick. The offsets are from the client's cell, and GGG's target is that cell plus the
    // offset: where the cursor is (median 0.8 cells off over 5 300 requests, moving or not). The newest request wins. A request made during a
    // swing comes with u8 3, and is served like any other.
    public static void OnLockstepRequest(Player player, ushort id, byte[] bytes)
    {
        var here = LockstepClientCell(player, player.Connection.LockstepFrame?.End ?? AreaTime(player));
        if (id == InstanceProtocol.LockstepMoveStartId)
        {
            var click = InstanceProtocol.ReadLockstepMoveStart(bytes);
            // An object interaction owns this press. A ground pickup releases it once the item is gone; a stationary
            // interactable (chest/stash/waypoint/transition) releases it once the walk-up/interaction finishes.
            bool move = SkillsFor(player).ActionName(click.SkillId) == "Move";
            if (move && (click.Flags & InstanceTransfer.InitialPressFlag) == 0 && player.LockstepInteractionTarget is uint used
                && LockstepInteractionActive(player, used)) return;
            player.LockstepInteractionTarget = null;
            (int X, int Y) aim = (here.X + click.Dx, here.Y + click.Dy);
            player.LockstepUse = null;
            if (move)
            {
                player.LockstepSkill.Clear(finishChannel: true);
                player.LockstepHold.Press(aim, click.SkillId, click.Flags, (click.Dx, click.Dy));
            }
            else
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x018e skill 0x{click.SkillId:x4} at ({aim.X}, {aim.Y}), flags 0x{click.Flags:x4}, u8 {click.Extra}");
                player.LockstepHold.Clear();
                if (player.ChannelSkill == click.SkillId && HeldLockstepAimedChannel(player))
                {
                    player.LockstepSkill.Retarget(null, aim);
                    player.LockstepChannelAimPending = true;
                }
                else player.LockstepSkill.Press(aim, null, click.SkillId, click.Flags, LockstepRepeats(player, click.SkillId));
            }
        }
        else if (id == InstanceProtocol.LockstepMoveUpdateId)
        {
            var (dx, dy) = InstanceProtocol.ReadLockstepMoveUpdate(bytes);
            player.LockstepHold.Update((here.X + dx, here.Y + dy), (dx, dy));
            player.LockstepSkill.Update((here.X + dx, here.Y + dy));
            if (HeldLockstepAimedChannel(player)) player.LockstepChannelAimPending = true;
            else if (player.ChannelSkill != 0 && player.LockstepSkill.Channel == player.ChannelSkillSequence)
            {
                player.LastChannelUpdateAt = AreaTime(player);
                SnipeAimedAt(player, (here.X + dx, here.Y + dy));
            }
        }
        else if (id == InstanceProtocol.LockstepMoveStopId)
        {
            player.LockstepHold.Release();
            player.LockstepSkill.Release();
        }
        else if (id == InstanceProtocol.LockstepSkillStopId)
        {
            if (InstanceProtocol.ReadLockstepSkillStop(bytes) == player.LockstepSkill.Skill) player.LockstepSkill.Release();
        }
        else if (id == InstanceProtocol.LockstepUseOnObjectId)
        {
            var (target, skill, flags, _) = InstanceProtocol.ReadLockstepUseOnObject(bytes);
            if (skill == 0x266 && (flags & InstanceTransfer.InitialPressFlag) == 0)
                lock (player.Npcs)
                    if (player.Npcs.ContainsKey(target)) return;
            player.LockstepInteractionTarget = null;
            player.LockstepHold.Clear();
            player.LockstepSkill.Clear(finishChannel: true);
            if (SkillsFor(player).IsGemSkill(skill) || skill == SkillCatalog.FirstSkillId)
            {
                var m = player.Area.Monsters.GetValueOrDefault(target);
                if (m is not { Hostile: true, Dead: false, OwnerId: null } && GroundPlaceableGemSkill(player, skill))
                {
                    if (ResolveObjectAimCell(player, target, m, AreaTime(player)) is { } cell)
                        player.LockstepSkill.Press(cell, null, skill, flags, target != player.Id && LockstepRepeats(player, skill));
                    return;
                }
                var aim = m?.Walk.Position(AreaTime(player)) ?? here;
                player.LockstepSkill.Press(aim, target, skill, flags,
                    (target != player.Id || SelfCastRepeats(player, skill)) && LockstepRepeats(player, skill));
            }
            else
            {
                player.LockstepUse = new(target, skill, 0, flags);
                if (skill == 0x266 && IsLockstepInteractable(player, target))
                    player.LockstepInteractionTarget = target;
            }
        }
    }

    static bool IsLockstepInteractable(Player player, uint target)
    {
        lock (player.Npcs) if (player.Npcs.ContainsKey(target)) return true;
        if (player.Instance.Ground.Get(target) is not null) return true;
        if (target == player.StashObject || target == player.GuildStashObject || target == player.CraftingBenchObject) return true;
        if (player.Layout.Waypoint is not null && target == WaypointCatalog.ObjectId) return true;
        if (player.Layout.AreaTransitions.Any(t => t.Id == target)) return true;
        if (player.QuestChests.ContainsKey(target)) return true;
        return false;
    }

    static bool LockstepInteractionActive(Player player, uint target)
    {
        if (player.Instance.Ground.WasTaken(target)) return false;
        lock (player.Npcs) if (player.Npcs.ContainsKey(target)) return true;
        if (player.Instance.Ground.Get(target) is not null) return true;
        if (target == player.StashObject || target == player.GuildStashObject || target == player.CraftingBenchObject) return true;
        if (player.Layout.Waypoint is not null && target == WaypointCatalog.ObjectId) return true;
        if (player.Layout.AreaTransitions.Any(t => t.Id == target)) return true;
        double now = player.Connection.LockstepFrame?.Start ?? AreaTime(player);
        return player.LockstepUse?.TargetId == target || player.Walk.Moving(now);
    }

    // A channelled skill starts once and runs until the release; anything else repeats while the button stays down.
    // A native channelled totem skill (Holy Flame Totem) is placed with a single cast by its owner, not channelled.
    // A skill cast on the player itself does not repeat while held (a buff or an aura would toggle again), except
    // Blade Vortex: a held key keeps adding blades at the cast rate, as in the original.
    static bool SelfCastRepeats(Player player, ushort skill) =>
        GemSkillEffects(player).FirstOrDefault(g => g.Id == skill) is { Name: not null } gem
        && IsBladeVortex(SkillsFor(player).EffectId(gem.Effect));

    static bool LockstepRepeats(Player player, ushort skill) =>
        !(GemSkillEffects(player).FirstOrDefault(g => g.Id == skill) is { Name: not null } gem
            && SkillsFor(player).IsChannelled(gem.Effect) && !IsTotemSkill(player, skill));

    static double LockstepScheduledAt(Player player, double fallback) =>
        player.Connection.LockstepClient && player.Connection.LockstepFrame is { } frame
            ? (player.LockstepActionScheduledAt >= frame.Start && player.LockstepActionScheduledAt <= frame.End
                ? player.LockstepActionScheduledAt : frame.Start)
            : fallback;

    static ushort NextLockstepSequence(Player player)
    {
        player.LockstepSequence = (ushort)((player.LockstepSequence + 1) & 0x7fff);
        return (ushort)(player.LockstepSequence | 0x8000);
    }

    // A Move can cancel at either end of a spell. A different spell can replace it only early; a held repeat waits.
    // <paramref name="forMove"/>: the caller is about to start a Move, which a totem placement's hold does not
    // refuse. The client's own move press is its word that its cast clip is over, so taking it cannot start a Move
    // the client is still standing through. The skill's own readiness keeps the strict hold, so a held key's repeat
    // does not take the frame the Move claims.
    public static bool LockstepPlayerFree(Player player, double at, bool forMove = false)
    {
        if (ShieldChargeTraveling(player, at)) return false;
        if (!MeleeRepeatAllowsMove(player, at)) return false;
        if (at >= player.LockstepBusyUntil) return true;
        if (!player.Connection.LockstepClient) return false;
        // A totem placement's hold does not refuse the client's own Move: the press the client sends once its cast
        // clip is over claims the frame, while the skill's own hold is untouched.
        // Any other busy extension (a pickup, a charge, a get-up) pushes the busy time past the totem hold's own end.
        if (forMove && at < player.LockstepTotemHoldUntil && player.LockstepBusyUntil <= player.LockstepTotemHoldUntil + 1e-9)
            return true;
        if (player.LockstepCastWindow is not { } cast) return false;
        if (player.LockstepHold.Active)
            return cast.AllowsMove(at, earlyCancellation: (player.LockstepHold.Flags & 0x400) != 0);
        if (!player.LockstepSkill.Pending || (player.LockstepSkill.Flags & 0x400) == 0) return false;
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == player.LockstepSkill.Skill);
        return gem.Name is not null && SkillsFor(player).IsSpell(gem.Effect) && !SkillsFor(player).IsAttack(gem.Effect)
            && cast.AllowsSkill(at, SkillsFor(player).ActionHash(gem.Effect));
    }

    // The frame pass: starts what the player's requests call for, if anything, at the frame's start. Blocked steps are
    // replayed first, so a walk stopped by a body counts as stopped in this frame. Sequences go up by one with the top bit
    // set (5 058 of 5 058 on GGG's server). A lockstep client's 0x1ab always starts an action, and puts the player on the
    // reported cell whatever the distance (0x141e95080), which is what keeps the two copies together.
    public static async Task StartLockstepActions(Player player, double frameStart)
    {
        (int X, int Y) target = default, from = default;
        int action = 0;
        ushort sequence = 0, ended = 0;
        (ushort Sequence, (int X, int Y) Aim)? channelAim = null;
        (int X, int Y)? channelLanding = null;
        bool skill = false, moveFromClient = false;
        int skillActionBefore = 0;
        double skillBusyBefore = 0, skillBusySet = 0, skillStartedBefore = 0, skillScheduledBefore = 0, skillTotemBefore = 0;
        double at;
        InstanceTransfer.UseSkillOnObject? use = null, staleUse = null;
        using (ProjectileMutation(player, phase: "lockstep"))
        {
            if (player.Closed || player.LeavingArea) return;
            // What a dead player asked for dies with it: a skill kept for the revival moved it off its checkpoint.
            if (player.PlayerLife == 0)
            {
                EndShieldChargeIfInterrupted(player);
                player.LockstepHold.Clear();
                player.LockstepSkill.Clear();
                player.LockstepChannelAimPending = false;
                player.LockstepCastWindow = null;
                player.LockstepTotemHoldUntil = 0;
                player.LockstepActionStartedAt = 0;
                player.LockstepActionScheduledAt = 0;
                player.LockstepActionBackswingUntil = 0;
                player.LockstepUse = null;
                return;
            }
            // A predictive client keeps its own walk: the frame pass is a lockstep client's only.
            if (!player.Connection.LockstepClient) return;
            at = Math.Max(frameStart, player.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
            // Lying on the beach (WashedUp), whatever the client asks gets it up instead (0x141e9b860): the server starts
            // the get-up, never the walk or use asked for, and WakeUpOnBeach holds the player until its clip has played.
            if (LyingOnBeach(player) && (player.LockstepHold.Active || player.LockstepUse is not null || player.LockstepSkill.Pending))
            {
                player.LockstepHold.Clear();
                player.LockstepSkill.Clear();
                player.LockstepUse = null;
                sequence = NextLockstepSequence(player);
            }
            StepWalkers(player, frameStart);
            RecordLockstepTrail(player, frameStart);
            var channelAimAtRelease = player.LockstepSkill.Aim;
            if (player.LockstepSkill.ChannelReleased() is { } channel)
            {
                if (player.LockstepChannelAimPending)
                {
                    AimChargedDash(player, channelAimAtRelease, at);
                    AimBladeFlurry(player, channelAimAtRelease);
                    AimPulseChannel(player, channelAimAtRelease);
                }
                ended = channel;
                player.LockstepChannelAimPending = false;
                channelLanding = EndChannel(player, at);
            }
            else if (player.LockstepChannelAimPending && HeldLockstepAimedChannel(player)
                && !PlayerActionBlocked(player, at, at))
            {
                var aim = player.LockstepSkill.Aim;
                if (player.LastLockstepChannelAim is { } last && aim == last.Aim)
                    player.LockstepChannelAimPending = false;
                else if (player.LastLockstepChannelAim is not { } previous || at - previous.At >= MovingChannelAimInterval)
                {
                    ChannelAimedAt(player, aim.X, aim.Y, at);
                    player.LastLockstepChannelAim = (aim, at);
                    player.LockstepChannelAimPending = false;
                    channelAim = (player.ChannelSkillSequence, aim);
                }
            }
            // Readiness belongs to the client's frame. Projectile time may already be ahead of it; using that
            // clock can start a Move before pickup completion, whose cleanup then discards the new path.
            double frameLen = FrameMs / 1000.0;
            double frameEnd = player.Connection.LockstepFrame?.End ?? (frameStart + frameLen);
            double queueHorizon = frameEnd - 1e-4;
            // Readiness belongs to the skill: a totem placement's hold still paces a held key's repeat and the
            // object use. A Move taken inside that hold does not end it, so the next cast still waits for the
            // client's own cadence.
            bool free = LockstepPlayerFree(player, frameStart);
            bool moveFree = free || LockstepPlayerFree(player, frameStart, forMove: true);
            // Client AnimationController::AdvanceClip (0x141dbf670) carries exact leftover frame dt into the next action
            // when a 0x01ab is pre-queued in Actor +0x1c0 before the running clip ends mid-frame, while Actor +0x29c
            // enforces at most one 0x01ab start per 33 ms tick (30.303 actions/s). Only early-queue once the previous
            // action has actually begun (frameStart >= LockstepActionStartedAt) so +0x1c0 is never overwritten.
            bool canQueueSkill = !ShieldChargeTraveling(player, frameStart)
                && frameStart >= player.LockstepActionStartedAt - 1e-9
                && player.LockstepBusyUntil <= queueHorizon
                && player.LockstepActionBackswingUntil <= queueHorizon;
            bool skillFree = free || canQueueSkill;
            if (!PlayerActionBlocked(player, frameStart, frameStart) && (skillFree || moveFree))
            {
                // Only the skill's own readiness reaches the target and pending-request clean-up below: a Move taken
                // inside a totem placement's hold does not touch the held skill's request.
                if (skillFree && player.LockstepSkill.Target is { } aimedAt && aimedAt != player.Id
                    && !(player.Area.Monsters.TryGetValue(aimedAt, out var m) && !m.Dead && m.Life > 0))
                    player.LockstepSkill.LoseTarget();
                // Actor's gate (0x141e941a0 -> 0x141e9479e / 0x141e947aa) lets only Move (0x2909 / 0xc266) cut in after
                // an attack's contact or while its cancelled backswing lockout (+0x2a0) runs; non-Move uses (object
                // pickups, spells, attacks) wait for the action to finish (NextAttackAt / LockstepActionBackswingUntil).
                bool attackReadyForUse = frameStart + 1e-9 >= player.NextAttackAt
                    && frameStart + 1e-9 >= player.LockstepActionBackswingUntil;
                bool earlySkillCancel = free && player.LockstepCastWindow is { } cw
                    && frameStart < cw.Start + cw.Duration;
                bool attackReadyForSkill = earlySkillCancel
                    || (player.NextAttackAt <= queueHorizon && player.LockstepActionBackswingUntil <= queueHorizon);
                bool canStartSkill = LockstepSkillCanStart(player, player.LockstepSkill.Skill);
                if (skillFree && !canStartSkill && player.LockstepSkill.Pending) player.LockstepSkill.ClearPending();
                // Pinned (a spike trap's risen spikes: Pins.cs): no walk starts, and a use waits, as it walks up.
                bool pinned = Pinned(player, at);
                if (free && !pinned && attackReadyForUse && player.LockstepUse is { } candidate
                    && candidate.SkillId == 0x266 && player.Instance.Ground.WasTaken(candidate.TargetId))
                {
                    player.LockstepUse = null;
                    staleUse = candidate;
                }
                if (free && !pinned && attackReadyForUse && player.LockstepUse is { } object_)
                {
                    player.LockstepCastWindow = null;
                    player.LockstepUse = null;
                    use = object_;
                    from = player.Walk.Position(frameStart);
                    sequence = NextLockstepSequence(player);
                }
                else if (canStartSkill && attackReadyForSkill
                    && player.LockstepSkill.Due(skillFree, repeat: player.LockstepAttack is null))
                {
                    skill = true;
                    sequence = NextLockstepSequence(player);
                    skillActionBefore = player.ActionCount;
                    skillBusyBefore = player.LockstepBusyUntil;
                    skillStartedBefore = player.LockstepActionStartedAt;
                    skillScheduledBefore = player.LockstepActionScheduledAt;
                    skillTotemBefore = player.LockstepTotemHoldUntil;
                    HoldLockstepSkill(player, sequence, frameStart, at);
                    skillBusySet = player.LockstepBusyUntil;
                }
                else if (moveFree && !pinned && LockstepMoveDue(player, frameStart) is { } due)
                {
                    target = due;
                    moveFromClient = StrikeRunning(player, frameStart);
                    EndShieldChargeIfInterrupted(player);
                    CancelMeleeRepeats(player, at);
                    action = Interlocked.Increment(ref player.ActionCount);
                    player.ChannelSkill = 0;
                    // Late Move-cancel after a spell's contact sets Actor +0x2a0 to the remaining cast duration
                    // (0x141e947d0 -> 0x141942f00), gating subsequent non-Move actions while allowing Move.
                    if (player.LockstepCastWindow is { } cast && frameStart >= cast.Start + cast.LateFrom)
                        player.LockstepActionBackswingUntil = Math.Max(player.LockstepActionBackswingUntil, cast.Start + cast.Duration);
                    player.LockstepCastWindow = null;
                    // A Move taken inside a totem placement's hold does not end that hold: the next cast still
                    // waits for the client's own cadence, so the held key cannot repeat on the next frame and stop
                    // the walk again at once.
                    if (free) player.LockstepBusyUntil = frameStart;
                    sequence = NextLockstepSequence(player);
                }
            }
        }
        if (sequence != 0 && LyingOnBeach(player))
        {
            await WakeUpOnBeach(player, null, sequence, 0, at);
            return;
        }
        // GGG ends a channel with 0x1ae on the release (35 ms later).
        if (ended != 0) await SendWorldRecord(player, player.Id, ChannelFinishedRecord(player, ended, channelLanding), $"0x01ae channel 0x{ended:x4} ended");
        if (channelAim is { } aimed) await SendChannelAim(player, aimed.Sequence, aimed.Aim);
        if (staleUse is not null) _ = OnUseObject(player, staleUse, at);
        if (sequence == 0) return;
        if (skill) StartLockstepSkill(player, sequence, at, skillActionBefore, skillBusyBefore, skillBusySet, skillStartedBefore, skillScheduledBefore, skillTotemBefore);
        else if (use is not null)
        {
            // The client walks up on this 0x1ab, where a predictive client starts by itself; the server's use goes on as
            // ever from here, its own walk included. Place from at the send gate so it matches the frame's start.
            await SendWorldRecord(player, player.Id, [], $"0x01ab lockstep use of object 0x{use.TargetId:x}", atGate: () =>
            {
                using (ProjectileMutation(player, phase: "send"))
                {
                    double gateAt = player.Connection.LockstepFrame?.Start ?? frameStart;
                    StepWalkers(player, gateAt);
                    from = player.Walk.Position(gateAt);
                    return LockstepObjectStart(player.Id, from, use, sequence);
                }
            });
            _ = OnUseObject(player, use with { Sequence = sequence, Started = true }, at);
        }
        else
        {
            bool moved = false;
            await SendWorldRecord(player, player.Id, [], $"0x01ab lockstep walk to ({target.X}, {target.Y})",
                atGate: () =>
                {
                    using (ProjectileMutation(player, phase: "send"))
                    {
                        if (!PlayerActionCurrent(player, action)) return null;
                        double gateAt = player.Connection.LockstepFrame?.Start ?? frameStart;
                        StepWalkers(player, gateAt);
                        if (PlaceLockstepPlayerMove(player, target, gateAt, moveFromClient) is not { } placed) return null;
                        from = placed;
                        moved = true;
                        return LockstepActionStart(player, from, target, sequence);
                    }
                },
                still: () => PlayerActionCurrent(player, action));
            if (moved) await CloseInteractionsIfMovingAway(player, target, AreaTime(player));
        }
    }

    // The estimate runs ahead of the client -- a round trip and the client's two frames (LockstepClientCell) -- and an
    // action's 0x1ab puts the client on its cell pair at any distance, so a pair taken from the estimate moves him. So
    // an attack (on a monster or on the ground) always starts from the cell the client stands on. A Move does so only
    // while a strike's walk-up or the busy time of the last action (LockstepBusyUntil: a swing, a cast, a pickup, a
    // hit's recovery) still runs (in a live move -> strike -> move chain the walk started at the strike's walk-up goal
    // while the client stood elsewhere); after a plain walk it is left on the estimate: the client keeps its own cell
    // through a walk (it takes a Move only from 3 cells away).
    public static bool StrikeRunning(Player player, double at) =>
        player.LockstepAttack is { Open: true } || at < player.LockstepBusyUntil;

    // Puts the server's copy of the player on the client's cell (LockstepClientCell) before an action is placed from it,
    // and returns that cell. A correction of the estimate, not a displacement: it is not counted as a teleport
    // (PlayerWalk.Place), so Intensity and Momentum keep their stacks. Under conn.Monsters.
    public static (int X, int Y) PlaceEstimateOnClient(Player player, double at)
    {
        var client = LockstepClientCell(player, at);
        if (client != player.Walk.Position(at)) player.Walk.Place(client.X, client.Y, at);
        return client;
    }

    // A player Move due at `at`: placed from the walker's frame-start cell (matching how the lockstep client applies
    // S→C 0x01ab at the start of frame N after stepping frames 0..N-1 from the TCP stream; rewinding `from` to
    // LockstepClientCell rubberbands the client backward by RTT + 2 frames). The client admits a Move by vtable slot
    // +0x130 (0x141949a20): dx*dx + dy*dy >= 9 from the walker's own cell.
    public static (int X, int Y)? PlaceLockstepPlayerMove(Player player, (int X, int Y) target, double at, bool fromClient = false)
    {
        var here = player.Walk.Position(at);
        if (!LockstepTakesMove(here, target)) return null;
        return player.Walk.Order(target.X, target.Y, at);
    }

    static readonly double LockstepHoldLookaheadSeconds = FrameMs / 1000.0 + 0.005;

    static bool LockstepHeldDirection((int Dx, int Dy) off) => (long)off.Dx * off.Dx + (long)off.Dy * off.Dy >= 4;

    static (int X, int Y) ExtendCloseHeldMove(Player player, (int X, int Y) here, (int Dx, int Dy) off)
    {
        // A 2-4 cell target finishes in 50-100 ms (faster than the 69-99 ms 0x0192 cadence) and would either stall on
        // the client or trigger 33 ms 0x01ab integer-cell snaps. Scale close held offsets along their exact direction
        // to ~140 ms of travel (~5-6 cells at base speed) so walks stay smooth at the normal 0x0192 cadence.
        double dist = Math.Sqrt((long)off.Dx * off.Dx + (long)off.Dy * off.Dy);
        double minDist = Math.Clamp(player.Walk.Speed * 0.14, 5.0, 12.0);
        if (dist >= minDist) return (here.X + off.Dx, here.Y + off.Dy);
        double scale = minDist / dist;
        return (here.X + (int)Math.Round(off.Dx * scale), here.Y + (int)Math.Round(off.Dy * scale));
    }

    /// <summary>
    /// Chooses the lockstep Move target due at <paramref name="frameStart"/>. While held, stops cleanly on the
    /// character's feet (<c>|offset| &lt; 2</c>), extends very close offsets along their exact direction so walks last
    /// across the 69–99 ms <c>0x0192</c> cadence without 33 ms integer-cell snaps or stutters, projects forward when
    /// lagged <see cref="LockstepClientCell"/> lands shorter than <c>|offset|</c> or behind <c>here</c>, and extends
    /// within 1 frame of arrival if <c>0x0192</c> is delayed.
    /// </summary>
    public static (int X, int Y)? LockstepMoveDue(Player player, double frameStart)
    {
        var here = player.Walk.Position(frameStart);
        bool walking = player.Walk.Moving(frameStart);
        if (player.LockstepHold.Due(walking, here) is { } due)
        {
            if (player.LockstepHold.Held && player.LockstepHold.Offset is { } off)
            {
                if (!LockstepHeldDirection(off)) return null;
                double minDist = Math.Clamp(player.Walk.Speed * 0.14, 5.0, 12.0);
                long dx = due.X - here.X, dy = due.Y - here.Y;
                long dot = dx * off.Dx + dy * off.Dy;
                long dueDist2 = dx * dx + dy * dy;
                long offDist2 = (long)off.Dx * off.Dx + (long)off.Dy * off.Dy;
                if (dot > 0 && LockstepTakesMove(here, due) && dueDist2 >= offDist2 && dueDist2 >= minDist * minDist)
                    return due;
                var projected = ExtendCloseHeldMove(player, here, off);
                player.LockstepHold.SetTarget(projected);
                return projected;
            }
            return LockstepTakesMove(here, due) ? due : null;
        }
        if (walking && player.LockstepHold.Held && player.LockstepHold.Offset is { } heldOff
            && LockstepHeldDirection(heldOff)
            && player.Walk.ArrivesAt - frameStart <= LockstepHoldLookaheadSeconds)
        {
            var projected = ExtendCloseHeldMove(player, here, heldOff);
            player.LockstepHold.SetTarget(projected);
            return projected;
        }
        return null;
    }

    // A skill asked on a monster: GGG answers the request with the action's start in the next frame, from the player's
    // cell and aimed at the monster (0x18f gets its 0x1ab with flag 0x80 and the target 36 to 54 ms later), and the client's
    // own action walks up and swings when it gets there, as a monster's chase does; a held skill then repeats once per
    // action. A predictive client starts the walk-up itself and only hears the swing. So on a lockstep client the start
    // goes out now, and its walk-up is the client's: placed where the 0x1ab goes out (LockstepAttackAtGate) and run by the
    // step pass (StepFrames), which ends it in the swing the client's own goal test starts (LockstepAttack.Decided), with
    // no second 0x1ab. Null when the client is not in lockstep: the swing says it, as before.
    public static async Task<LockstepAttack?> LockstepAttackStart(Player player, InstanceTransfer.UseSkillOnObject use, SpawnedMonster m, SkillCatalog.Unarmed weapon, int precision,
        int action, string what)
    {
        if (!player.Connection.LockstepClient) return null;
        LockstepAttack attack;
        player.LockstepAttack?.Fails();
        player.LockstepAttack = attack = new(action, use.TargetId, m, WalkUpRange(weapon), m.StopRange - 2, precision,
            () => LockstepAttackCutIn(player, use.SkillId, use.Sequence))
        {
            InPlace = (use.Flags & 1) != 0,
            AttackSeconds = () => LockstepAttackSeconds(player, use.SkillId, use.Sequence),
        };
        await SendWorldRecord(player, player.Id, [], $"0x01ab lockstep {what} 0x{use.SkillId:x4} on 0x{use.TargetId:x}",
            atGate: () =>
            {
                using (ProjectileMutation(player, phase: "send")) return LockstepAttackAtGate(player, attack, use);
            });
        // Never placed (the gate dropped it): nothing walks up. Decided already, this changes nothing.
        if (!attack.Open) attack.Fails();
        return attack;
    }

    // The attack's 0x1ab at its send gate. The client applies it at the start of the frame being sent, so its walk-up is
    // placed there, after the steps the walk the client still runs takes before it (StepWalkers), from the cell they leave
    // the player on, as a monster's order is (LockstepOrderFrom; 1 446 of 1 447 traced setups of the player's actions came
    // in the frame of an order's apply). Null, and the attack failed, when another action replaced it. Under conn.Monsters.
    public static byte[]? LockstepAttackAtGate(Player player, LockstepAttack attack, InstanceTransfer.UseSkillOnObject use)
    {
        if (player.LockstepAttack != attack || !PlayerActionCurrent(player, attack.Action)
            || !LockstepSkillCanStart(player, use.SkillId))
        {
            attack.Fails();
            return null;
        }
        double at = player.Connection.LockstepFrame?.Start ?? ProjectileWorldTime(player);
        StepWalkers(player, at);
        var (here, aim) = PlaceLockstepAttack(player, attack, at);
        string walkUp = attack.Open ? $"walks up to ({player.Walk.Destination.X}, {player.Walk.Destination.Y})"
            : attack.Decided.Result is null ? "no path" : "swings at once";
        player.Connection.Performance.Count(attack.Open ? "lockstep use walks up" : attack.Decided.Result is null ? "lockstep use no path" : "lockstep use swings at once");
        Console.WriteLine($"[gateway] {player.Connection.Remote} lockstep skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} on 0x{use.TargetId:x} at {at:0.000}: " +
            $"player ({here.X}, {here.Y}), monster ({aim.X}, {aim.Y}), distance {Dist(here, aim):0.0}; {walkUp}");
        return InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, aim.X, aim.Y, use.SkillId, use.Sequence, use.Flags,
            targetObjectId: use.TargetId);
    }

    // The attack's MoveTo placed at a lockstep frame's start `at`, toward the monster's own cell (0x14123f9c0), which the
    // apply reads before any object ticks. MoveTo tests its goal where the player stands first (0x141e81c20 →
    // 0x141e81950): met, the swing begins in this frame, from where the frame's steps leave a walking player
    // (0x141e82310 → 0x141e38420 tests that same cell, then stops after the steps, even when they carry him out of reach);
    // nothing found ends the action (0x141e82010 → 0x141e82310); else the step pass runs the walk-up. Under conn.Monsters.
    // Flag 0x0001 is attack in place: the client sets it on its request (0x1401214f0: the attack-in-place key, a skill
    // set to always attack in place, or a use the input handler goes on with) and the 0x1ab echoes it. The Actor's start
    // then asks MoveTo for the actor's own cell, ranges 0 (0x141e95400), instead of the target with the weapon's range:
    // in goal at once whatever the distance, so the walk under way ends with the stop that waits for the tick and the
    // swing begins in this frame from where its steps leave the player. The hit asks the reach as ever. Live 2026-11-10:
    // after such a request 27 cells from the monster the client's player took the frame's step and stood through the
    // swing, while an estimate that walked up went 11 cells on and its next order put the client's player there.
    public static ((int X, int Y) Here, (int X, int Y) Aim) PlaceLockstepAttack(Player player, LockstepAttack attack, double at)
    {
        var aim = attack.AimAt(at);
        if (attack.InPlace)
        {
            var stand = player.Walk.Position(at);
            player.Walk.Cancel(at);
            attack.TimerFrom = at;
            LockstepAttackSwings(player, attack, at, player.Connection.LockstepFrame?.End ?? at);
            return (stand, aim);
        }
        var here = player.Walk.Approach(aim.X, aim.Y, attack.Reach, at, attack.Tolerance, attack.Precision).Here;
        attack.TimerFrom = at;
        if (player.Walk.InReachOf(here, aim, attack.Reach, attack.Tolerance))
            LockstepAttackSwings(player, attack, at, player.Connection.LockstepFrame?.End ?? at);
        else if (player.Walk.Reached && !player.Walk.ReachedGoal) attack.Fails();
        else attack.Open = true;
        return (here, aim);
    }

    public static bool LockstepGroundMelee(Player player, ushort skill, ushort flags)
    {
        if ((flags & 1) != 0 || !WeaponProfileFor(player, activeSet: true).Melee) return false;
        if (skill == SkillCatalog.FirstSkillId) return true;
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skill);
        return gem.Name is not null && !IsTotemSkill(player, skill) && !SkillsFor(player).IsChannelled(gem.Effect)
            && SkillsFor(player).IsAttack(gem.Effect) && AttackUsesWalkUp(player, gem.Effect)
            && PlacedKind(SupportedSkill(player, skill)) is { Trap: false, Mine: false }
            && (!SkillsFor(player).IsProjectile(gem.Effect)
                || GatewayProjectileSkillProfiles.IsMoltenStrike(SkillsFor(player).EffectId(gem.Effect)));
    }

    // Actor starts a ground melee use (default attack or melee attack gem) with MoveTo too (0x141e95400 -> 0x141e81c20):
    // weapon range minus one, tolerance -1, precision 1. Only flag 1 forces a swing in place. Its fixed aim shares the
    // monster walk-up's frame stepping and cancellation; the swing hold, cost, and attack timer start when the walk-up
    // reaches its goal (immediately if already in reach), with no second 0x1ab.
    public static async Task StartLockstepGroundMelee(Player player, (int X, int Y) aim, ushort skill, ushort sequence, ushort flags)
    {
        if (InTown(player)) return;
        double now = ProjectileWorldTime(player);
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0 || PlayerActionBlocked(player, now, now)) return;
        var gem = skill == SkillCatalog.FirstSkillId ? default : GemSkillEffects(player).FirstOrDefault(g => g.Id == skill);
        int action = 0;
        CooldownUseTicket? approachTicket = null;
        if (gem.Name is not null)
        {
            if (GemSkillInstanceDisabled(player, gem.Effect, skill) is not null) return;
            if (!ProjectilePathAvailable(player, skill, gem.Effect)) return;
            if (!AttackReady(player, skill, gem.Effect, gem.Level, out _, sequence)) return;
            if (!TryBeginSkillCooldownUse(player, skill, gem.Effect, CooldownForSkill(player, skill), false, () =>
            {
                now = ProjectileWorldTime(player);
                CancelMeleeRepeats(player, now);
                action = Interlocked.Increment(ref player.ActionCount);
                player.ChannelSkill = 0;
            }, out approachTicket, reserve: false, requestedAt: now)) return;
        }
        else
        {
            CancelMeleeRepeats(player, now);
            action = Interlocked.Increment(ref player.ActionCount);
            player.ChannelSkill = 0;
        }
        var weapon = gem.Name is not null ? HandWeapon(player, AttackHandFor(player, skill, sequence)) : WeaponFor(player);
        if (gem.Name is not null)
            weapon = weapon with { Range = MeleeSkillReach(player, weapon, gem.Effect, SupportedSkill(player, skill).Stats) };
        LockstepAttack attack;
        player.LockstepAttack?.Fails();
        player.LockstepAttack = attack = new(action, 0, null, WalkUpRange(weapon), -1, 1,
            () => LockstepAttackCutIn(player, skill, sequence))
        {
            GroundTarget = aim,
            GroundAttackSeconds = gem.Name is null ? () => LockstepAttackSeconds(player, skill, sequence) : null,
            AttackSeconds = () => LockstepAttackSeconds(player, skill, sequence),
        };
        try
        {
            await EndGracePeriod(player, action: true);
            await SendWorldRecord(player, player.Id, [], $"0x01ab lockstep ground {(gem.Name ?? "attack")} 0x{skill:x4}", atGate: () =>
            {
                using (ProjectileMutation(player, phase: "send"))
                {
                    // A use whose cost can no longer be paid sends nothing (no walk-up, no 0x1ab): the frame pass
                    // checked it before starting the skill, but the send waits for the wire's turn, so check again here.
                    if (player.LockstepAttack != attack || !PlayerActionCurrent(player, attack.Action)
                        || !LockstepSkillCanStart(player, skill))
                    {
                        attack.Fails();
                        return null;
                    }
                    double at = player.Connection.LockstepFrame?.Start ?? ProjectileWorldTime(player);
                    StepWalkers(player, at);
                    var placed = PlaceLockstepAttack(player, attack, at);
                    return InstanceProtocol.ActionStarted(player.Id, placed.Here.X, placed.Here.Y, aim.X, aim.Y,
                        skill, sequence, flags);
                }
            });
            if (!attack.Open) attack.Fails();
            var swung = await attack.Decided;
            if (swung is not { } decided || gem.Name is null) return;
            if (player.Closed || player.LeavingArea || !PlayerActionCurrent(player, action)
                || approachTicket?.Current(player) == false) return;
            if (!TryBeginSkillCooldownUse(player, skill, gem.Effect, CooldownForSkill(player, skill), false,
                () => { }, out var cooldownUse, actionToken: action))
            {
                if (player.LockstepBusyUntil > decided.Start) player.LockstepBusyUntil = decided.Start;
                return;
            }
            LockstepAttackOver(player, action);
            double? swingAt = decided.Start;
            bool projectileRuntime = UsesProjectileRuntime(gem.Effect);
            Task<bool>? attackTask = projectileRuntime ? CastGemSkill(player, skill, decided.From, aim,
                sequence: sequence, actionToken: action, cooldownUse: cooldownUse, swingAt: swingAt) : null;
            if (attackTask is { IsCompletedSuccessfully: true, Result: false }) return;
            bool acknowledged = SkillAcknowledgementAllowed(player, gem.Effect, skill,
                () => cooldownUse?.CommitNativeUse(player, skill) ?? PlayerActionCurrent(player, action));
            if (acknowledged) await SendNativeCooldownRefresh(player);
            if (!projectileRuntime && acknowledged) _ = CastGemSkill(player, skill, decided.From, aim,
                sequence: sequence, actionToken: action, cooldownUse: cooldownUse, swingAt: swingAt);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        finally { LockstepAttackOver(player, attack.Action); }
    }

    // A spell or attack advances in its first native frame and carries leftover dt across 33 ms ticks when the next
    // 0x01ab is pre-queued in Actor +0x1c0 (0x141dbf670). Preserve the continuous start time inside [frameStart, frameEnd]
    // so both attacks and spells match Predictive APS/CPS up to the client's 1-start-per-frame (+0x29c) ceiling.
    public static void HoldLockstepSkill(Player player, ushort sequence, double frameStart, double at)
    {
        bool monster = player.LockstepSkill.Target is { } target && target != player.Id;
        ushort requested = player.LockstepSkill.Skill;
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == requested);
        bool spell = gem.Name is not null && SkillsFor(player).IsSpell(gem.Effect) && !SkillsFor(player).IsAttack(gem.Effect);
        bool thrown = gem.Name is not null && PlacedKind(SupportedSkill(player, requested)) is not { Trap: false, Mine: false };
        bool isAttack = requested == SkillCatalog.FirstSkillId
            || gem.Name is not null && !thrown && !IsTotemSkill(player, requested) && SkillsFor(player).IsAttack(gem.Effect);
        double frameLen = FrameMs / 1000.0;
        double frameEnd = player.Connection.LockstepFrame?.End ?? (frameStart + frameLen);
        bool earlyCancel = player.LockstepCastWindow is { } cw && frameStart < cw.Start + cw.Duration
            && LockstepPlayerFree(player, frameStart);
        player.LockstepCastWindow = null;
        double prevEnd = earlyCancel ? frameStart
            : Math.Max(player.LockstepActionBackswingUntil, Math.Max(player.NextAttackAt, player.LockstepBusyUntil));
        double actualStart = prevEnd > frameStart && prevEnd <= frameEnd ? prevEnd : frameStart;
        player.LockstepActionStartedAt = actualStart;
        player.LockstepActionScheduledAt = actualStart;
        // Only skills that go through LockstepAttackStart or StartLockstepGroundMelee defer their hold until the
        // walk-up reaches its swing; immediate casts on a monster (spells, totems, traps/mines, movement skills, and
        // attack gems with "always attack without moving", Flags bit 0) hold from here.
        bool defersHold = monster
            ? requested == SkillCatalog.FirstSkillId
                || gem.Name is not null && !thrown && !IsTotemSkill(player, requested)
                    && SkillsFor(player).IsAttack(gem.Effect) && AttackUsesWalkUp(player, gem.Effect)
                    && (player.LockstepSkill.Flags & 0x1) == 0
            : LockstepGroundMelee(player, requested, player.LockstepSkill.Flags);
        if (defersHold)
        {
            player.LockstepTotemHoldUntil = 0;
            return;
        }
        double hold = LockstepActionHold(player, requested, sequence);
        if (spell)
        {
            player.LockstepTotemHoldUntil = 0;
            player.LockstepBusyUntil = actualStart + hold;
            player.LockstepCastWindow = LockstepSpellMoveWindow(player, requested, gem.Effect, actualStart, hold);
        }
        else if (hold <= 0)
        {
            player.LockstepTotemHoldUntil = 0;
            player.LockstepBusyUntil = actualStart;
        }
        else if (isAttack)
        {
            // LockstepAttackCutIn already reads the clip's lock_cancelling keyframe (or contact) from client.db;
            // hold directly to it (clamped to duration) without an extra +frameLen frame that makes Move-cancels sluggish.
            player.LockstepTotemHoldUntil = 0;
            double duration = LockstepAttackSeconds(player, requested, sequence);
            player.LockstepBusyUntil = Math.Min(actualStart + duration, actualStart + hold);
        }
        else
        {
            // A totem placement's hold: only a totem skill's hold lets the client's own Move through. Any other
            // skill reaching this branch leaves the flag off.
            player.LockstepBusyUntil = actualStart + hold;
            player.LockstepTotemHoldUntil = IsTotemSkill(player, requested) ? player.LockstepBusyUntil : 0;
        }
    }

    // Whether the requested skill can actually start right now (enabled and payable). Checked before HoldLockstepSkill
    // so an unpayable cast (e.g. insufficient mana) does not advance LockstepBusyUntil and block subsequent Moves.
    static bool LockstepSkillCanStart(Player player, ushort skillId)
    {
        if (skillId == 0) return false;
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is null) return true;
        if (GemSkillInstanceDisabled(player, gem.Effect, skillId) is not null) return false;
        if (SkillsFor(player).VaalBudget(gem.Effect, gem.Level) is not null) return true;
        if (PaysOnUse(player, skillId, gem.Effect) && !CanPaySkillCost(player, skillId)) return false;
        return true;
    }

    // How long a use started at the ground or on the player holds it: an attack until the client lets a Move cut in
    // (LockstepAttackCutIn), a spell repeat for its adjusted cast time, a channel not at all (it
    // runs until the release).
    public static double LockstepActionHold(Player player, ushort skill, ushort sequence)
    {
        if (GemSkillEffects(player).FirstOrDefault(g => g.Id == skill) is { Name: not null } gem)
        {
            var supported = SupportedSkill(player, skill);
            var placed = PlacedKind(supported);
            if (placed.Trap || placed.Mine)
            {
                var stats = CharacterPlayerStats(player);
                return Math.Max(TickSeconds, PlacedThrowSeconds(
                    n => GolemStat(supported.Stats, n) + GolemStat(stats, n),
                    placed.Mine, ChargesOf(player).Effective(ChargeType.Frenzy)) / PlayerActionFactor(player));
            }
            if (IsTotemSkill(player, skill))
                return Math.Max(TickSeconds, TotemPlacementSeconds(player, skill, gem.Effect));
            if (SkillsFor(player).IsChannelled(gem.Effect)) return 0;
            // The native spell duration divides the virtual base by cast speed (0x1419f1960).
            // The repeat counter restarts the same action (0x1419e6370); hold through every cast, not just its first.
            // Reuse the cast path's skill-specific speed and action speed; a refused cast holds no action.
            if (SkillsFor(player).IsSpell(gem.Effect) && !SkillsFor(player).IsAttack(gem.Effect))
                return (SpellTimingFor(player, gem.Effect, skill)?.DurationSeconds ?? 0)
                    * (1d + SpellRepeats(player, skill, gem.Effect)) / PlayerActionFactor(player);
            if (!SkillsFor(player).IsAttack(gem.Effect)) return SkillsFor(player).CastTimeMs(gem.Effect) / 1000d / PlayerActionFactor(player);
            // LeapSlam::CanCancel (0x1412faf60) clears +0x368 at hit_ground and allows cancellation at contact = hit_ground
            // (0x141942cf0). With startup skip (0x1419fda80, seek to leave_ground - 10 ms), the client lands at timing.Land
            // (~36% of attack time), not the unskipped clip fraction (~67-74%) which locked the player after landing.
            if (IsLeapSlam(player, gem.Effect))
            {
                try { return LeapSlamTiming(player, skill, gem.Effect, SupportedAttackSeconds(player, skill, sequence)).Land; }
                catch (InvalidOperationException) { }
            }
            // ShieldCharge::CanCancel (0x141a6fd50) blocks cancellation while traveling (+0x3e5 != 0), which LockstepPlayerFree
            // enforces dynamically via ShieldChargeTraveling (automatically shortening on body/wall stops); once arrived,
            // charge_*_end has contact = 0. No-charge uses (< 24 cells) set their own in-place bash hold in BeginShieldCharge.
            if (ShieldChargeBonusPct(player, skill, gem.Effect) != 0) return 0;
        }
        return LockstepAttackCutIn(player, skill, sequence);
    }

    static GameServer.Core.World.LockstepCastWindow? LockstepSpellMoveWindow(Player player, ushort skill, int effect, double start, double duration)
    {
        // These projectile actions share the clip gate (0x141942b50). Ordinary repeats allow its early window only
        // on the first cast and its late window only on the final cast (0x1419e6460).
        if (SkillsFor(player).EffectId(effect) is not ("Fireball" or "FreezingPulse") || duration <= 0) return null;
        int repeats = SpellRepeats(player, skill, effect);
        var own = SupportedStats(player, skill);
        bool Has(uint name) => name is var key && own.GetValueOrDefault(key) != 0;
        // These overrides bypass the repeat-stage gate; retain the full hold until their separate windows are modeled.
        if (repeats > 0 && (Has(Stat.MultistrikeDamagePlusPctFinalOnFirstRepeat)
            || Has(Stat.MultistrikeDamagePlusPctFinalOnSecondRepeat))) return null;
        bool cannotCancelBeforeContact = Has(Stat.CannotCancelSkillBeforeContactPoint);
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
        var window = Animations.SpellMoveWindow(ao, AnimationWeaponProfileFor(player), CastAnimationKind.Projectile,
            start, duration / (1d + repeats), cannotCancelBeforeContact);
        return window is { } cast ? cast with
        {
            Duration = duration,
            LateFrom = duration - cast.Duration + cast.LateFrom,
            ActionHash = SkillsFor(player).ActionHash(effect),
        } : null;
    }

    // The attack time of a held attack skill, as its handler paces it (Attack, CastGemSkill).
    // A dual-wielded use takes its own hand's time (AttackHands, from the use's sequence).
    static double LockstepAttackSeconds(Player player, ushort skill, ushort sequence = 0) => skill == SkillCatalog.FirstSkillId
        ? Math.Max(TickSeconds, DefaultAttackSeconds(player, sequence, CharacterPlayerStats(player)) / PlayerActionFactor(player))
        : ShieldWeapon(player, skill) is { } shieldWeapon
            ? SupportedOffHandAttackSeconds(player, skill, shieldWeapon)
            : StartingAttackSeconds(player, skill, sequence);

    // When, after a swing's start, the client lets a Move cut the attack short: its clip's lock_cancelling keyframe where
    // it has one, else its contact (live 2026-11-03, the first Move the client ran at once: a wand's 0.334 s into a
    // 0.667 s attack at a 0.300 s contact, a one-hand sword's 0.58 s into a 0.645 s attack at a 0.56 s contact, a bow's
    // 0.40 s into 0.714 s at its lock_cancelling, 600 of 1066 ms, with the contact at 0.27). A Move sent before that is
    // queued on the client until then (0x141e937b0 takes it, the actor runs it at the keyframe), while the server's
    // estimate would walk alone. The default attack's keyframes are its stance clip's (DefaultAttackClip.CutInSeconds);
    // an attack gem's cut-in uses its clip's lock_cancelling keyframe (or contact when none is listed, AttackCutIn).
    public static double LockstepAttackCutIn(Player player, ushort skill, ushort sequence = 0)
    {
        double seconds = LockstepAttackSeconds(player, skill, sequence);
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
        var weapon = AnimationWeaponProfileFor(player);
        if (skill == SkillCatalog.FirstSkillId)
            return Animations.DefaultAttackCutInSeconds(ao, weapon, sequence, seconds);
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skill);
        string? clip = gem.Name is not null && SkillsFor(player).EffectId(gem.Effect) is { } effectId
            ? SkillsFor(player).ActionHash(gem.Effect) == HolyHammersAction ? HolyHammersSourceClip(weapon)
                : SkillClipName(TransfiguredBase(effectId) ?? effectId)
            : null;
        return seconds * Animations.AttackCutIn(ao, weapon, clip);
    }

    // The walk-up is over: the swing began in the frame [start, end] (the order's own, or the step pass's goal frame), from
    // where the stop leaves the player. It holds him for the attack's Hold (until the client lets a Move cut in,
    // LockstepAttackCutIn) from that frame's end (live: a wand's first Move 0.333 s after the client's start was refused
    // at a 0.3335 s contact, 0.334 s taken), set here so a held Move in the same frame pass waits for it. Its attack time
    // is the one the swing starts with, as the actor's animation takes it then (0x141e9c700, a monster's +0x26c), so a
    // chill or a buff gone during the walk-up counts. Not traced for a walk-up's swing: no timer of the player's own
    // swing was found (Actor +0x26c stays 0), so it is dated as a monster's walk-up swing is (its +0x26c one frame down
    // at its frame's end, 48 of 48). Under conn.Monsters.
    public static void LockstepAttackSwings(Player player, LockstepAttack attack, double start, double end)
    {
        double swingStart = player.LockstepActionScheduledAt >= start && player.LockstepActionScheduledAt <= end
            ? player.LockstepActionScheduledAt : start;
        player.LockstepActionStartedAt = swingStart;
        double hold = attack.Hold();
        if (attack.GroundAttackSeconds is { } seconds)
        {
            double duration = seconds();
            player.NextAttackAt = AttackTiming.After(swingStart, player.NextAttackAt, duration);
            player.LockstepBusyUntil = Math.Max(player.LockstepBusyUntil, Math.Min(swingStart + duration, end + hold));
        }
        else if (attack.AttackSeconds is { } attackDuration)
        {
            double duration = attackDuration();
            player.LockstepBusyUntil = Math.Max(player.LockstepBusyUntil, Math.Min(swingStart + duration, end + hold));
        }
        else
            player.LockstepBusyUntil = Math.Max(player.LockstepBusyUntil, end + hold);
        attack.Swings(swingStart, end, player.Walk.Destination);
    }

    // The attack is over, swung or not: nothing more holds the held skill's next use.
    public static void LockstepAttackOver(Player player, int action)
    {
        if (player.LockstepAttack is { } attack && attack.Action == action)
        {
            attack.Fails();
            player.LockstepAttack = null;
        }
    }

    // C→S 0x192 on a channel: GGG answers with its new target (40 ms later). The channel's own hits run as ever.
    public static Task AimLockstepChannel(Player player)
    {
        (ushort Sequence, (int X, int Y) Aim)? channel;
        channel = player.LockstepSkill.Channel is { } sequence ? (sequence, player.LockstepSkill.Aim) : null;
        if (channel is { } current) AimChannelTriggers(player, current.Aim);
        return channel is { } c
            ? SendChannelAim(player, c.Sequence, c.Aim)
            : Task.CompletedTask;
    }

    static Task SendChannelAim(Player player, ushort sequence, (int X, int Y) aim) =>
        SendWorldRecord(player, player.Id, InstanceProtocol.ActionTarget(player.Id, sequence, aim.X, aim.Y),
            $"0x01ad channel aims at ({aim.X}, {aim.Y})");
}
