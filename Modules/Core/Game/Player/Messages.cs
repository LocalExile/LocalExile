using System.Diagnostics;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

public static partial class Game
{
    // Handles the framed messages of one read, ticks taken out (OnClientData):
    // 0x7d (area generated) → the local player id (0x22), the player object (0x2bb), inventories, passive slots, the area transitions,
    // and with Game_GATEWAY_ENTER=1 enter area (0x21) + wake zone; 0x25 (move) → 0x1ab echo;
    // 0x24 on an area transition → change area (0x2b4, then 0x27 with the new instance) or, with Ctrl, the instance chooser (0xae).
    // Anything else is logged in hex: those are the next messages to map.
    // The most a hold on the server has been seen to outlast the client's is 33 ms.
    const double HoldEndGraceSeconds = .06;

    // Where the player appears in the area, with the line that says why. Back through a town portal: where the portal
    // stands (OpenTownPortal). Waypoint travel: next to the destination's waypoint, which a generated area has only from
    // the walk-map recorder's capture for this seed (live.ps1 -Walkmaps). Otherwise next to the exit back; Resurrect at
    // Checkpoint and the monster packs' clearance use it too. Read when 0x1d goes out (the packs' plan) and at 0x7d.
    static (AreaLayout Layout, string? Note) ArrivalLayout(ClientState conn)
    {
        var layout = conn.Layout;
        if (conn.ArriveAt is var (sx, sy))
            return (layout with { SpawnX = sx, SpawnY = sy }, $"back at the portal ({sx}, {sy})");
        if (conn.ArriveAtWaypoint && layout.Waypoint is { } arrival)
        {
            var (ax, ay) = layout.Near((arrival.GridX, arrival.GridY));
            return (layout with { SpawnX = ax, SpawnY = ay }, $"arrived by waypoint: spawn ({ax}, {ay}) next to the waypoint at ({arrival.GridX}, {arrival.GridY})");
        }
        if (layout.Arrival(conn.CameFrom, conn.ArrivalEntrance) is var (ex, ey) && (ex, ey) != (layout.SpawnX, layout.SpawnY))
            return (layout with { SpawnX = ex, SpawnY = ey }, $"arrival from 0x{conn.CameFrom:x4}: ({ex}, {ey})");
        if (conn.ArriveAtWaypoint)
            return (layout, $"arrived by waypoint, but {layout.Id} seed {conn.Seed} has no captured waypoint " +
                $"(run live.ps1 -Walkmaps): spawn ({layout.SpawnX}, {layout.SpawnY}){(layout.Walkable is null ? ", no walk map either: the player may stand outside the map" : "")}");
        return (layout, null);
    }

    // How long the hold on the player still has to run, if it is within limit; else 0.
    static double HoldEndsWithin(ClientState conn, double limit)
    {
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            double left = Math.Max(conn.HitRecovery.Until, conn.ElementalAilments.FreezeUntil(now)) - now;
            return left <= limit ? Math.Max(left, 0) : 0;
        }
    }

    // Temporary lag instrumentation (remove with the investigation): this thread's CPU time tells a slow call from a
    // stall (waiting for a lock or the pool, a collection, the thread descheduled).
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    static long ThreadCpuMilliseconds() =>
        GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? (kernel + user) / 10_000 : 0;

    public static async Task OnClientMessages(IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, ClientState conn, long readAt = 0)
    {
        foreach (var (id, bytes) in messages)
        {
            if (conn.Closed) return;
            using var timing = conn.Performance.Message(id);
            // One character snapshot for this message: its validation, payment and launch read the same stats instead of
            // rebuilding the whole character five or six times (2026-11-05: 154 builds/s cost a full core).
            using var stats = new PlayerStatScope(conn);
            // Temporary lag instrumentation (remove with the investigation): which phase of a cast message takes its time.
            long useStarted = Stopwatch.GetTimestamp(), phaseAt = useStarted, useCpuStarted = ConnectionPerformance.LagProbe ? ThreadCpuMilliseconds() : 0;
            var phases = new List<string>();
            void Mark(string name)
            {
                if (!ConnectionPerformance.LagProbe) return;
                long now = Stopwatch.GetTimestamp();
                phases.Add($"{name}={Stopwatch.GetElapsedTime(phaseAt, now).TotalMilliseconds:0}");
                phaseAt = now;
            }
            // A terminal Hardcore owner may only leave for character selection.
            if (HardcoreProgressBlocked(conn) && id != 0x0153) continue;
            // A mod may take the message (Program.Mods.cs): then the Gateway's own handler below does not run.
            if (GameEvents.WantsPacket(id) && await GameEvents.AnyPacket(conn, id, bytes)) continue;
            // Immediate transfer ingress cuts the old epoch off instead of replaying overdue old-world damage.
            // Movement/combat handlers also advance atomically inside their mutation boundaries.
            bool transferIngress = id is InstanceTransfer.ChooseInstanceId or InstanceTransfer.LeaveAreaAckId
                or InstanceWaypoints.TravelId or InstanceWaypoints.VisitHideoutId or InstanceWaypoints.VisitDelveHubId or InstanceWaypoints.VisitMenagerieId or InstanceWaypoints.VisitSanctumId or InstanceWaypoints.VisitKingsmarchId or InstanceWaypoints.VisitHeistHubId or InstanceWaypoints.VisitChayulaHubId or InstanceHideouts.SelectId
                || id == InstanceProtocol.ResurrectId && InstanceProtocol.ReadResurrectInTown(bytes)
                || id == InstanceItems.CommandId && InstanceItems.ReadCommand(bytes).Id == InstanceItems.WarpToHideoutCommand;
            // Projectiles advance on their own 30 Hz pump. Advancing them again before every input message made a
            // Kinetic Blast burst's backlog of contacts (and the kills and loot they enqueue) run inside the input
            // handler: 0x0025 took 100-200 ms and the input queue reached 7 s (2026-11-05). Handlers that need a
            // current simulation use ProjectileMutation, which advances under the same locks.
            // The client's answer to 0x2b4 (0x14214efe0): it is ready to leave, so move it now.
            if (id == InstanceTransfer.LeaveAreaAckId)
            {
                if (conn.PendingTransfer is { } leaving) await Transfer(conn, leaving);
                continue;
            }
            // Native TakeHit blocks predicted action starts too. Buffered normal actions must not restart the server's
            // movement/cast timeline during that same recovery. Flasks and instant skills retain their own paths.
            if (id is InstanceProtocol.UseSkillAtLocationId or InstanceTransfer.UseSkillOnObjectId or InstanceProtocol.ChannelUpdateId
                or InstanceProtocol.ChannelUpdateAltId)
            {
                // The client buffers a click from the last part of a hold and sends it as its own hold ends, which can be a
                // few ms before the server's. On localhost it arrives inside them: dropped, the client acts alone.
                while (HoldEndsWithin(conn, HoldEndGraceSeconds) is > 0 and var left)
                {
                    await Task.Delay(TimeSpan.FromSeconds(left + .001));
                    readAt = 0;
                }
                lock (conn.Monsters)
                {
                    if (PlayerActionBlocked(conn, conn.ClockAt(readAt), conn.Clock.Elapsed.TotalSeconds))
                    {
                        // The client starts the newest click when it can again: the server walks it then (ReleaseHeldMove).
                        if (!InstanceProtocol.IsChannelUpdate(id))
                            conn.HeldMove = id == InstanceProtocol.UseSkillAtLocationId && InstanceProtocol.ReadUseSkillAtLocation(bytes) is var held
                                && SkillsFor(conn).ActionName(held.SkillId) == "Move" ? held : null;
                        continue;
                    }
                    conn.HeldMove = null;
                }
            }
            if (id == InstanceProtocol.UseSkillAtLocationId) Mark("hold");
            if (id == InstanceProtocol.UseSkillAtLocationId)
            {
                try
                {
                // Echo the action so the client runs it (Move on a ground click); an attack gem is paced and may be movement
                // (below). The client starts the action itself and waits for this echo before sending the next one.
                var use = InstanceProtocol.ReadUseSkillAtLocation(bytes);
                double at = conn.ClockAt(readAt);   // when the client applied the click, not when this handler runs
                Mark("read");
                if (use.SkillId == TutorialObjects.WashedUpSkillId)
                {
                    await WakeUpOnBeach(conn, (use.X, use.Y), use.Slot, use.Flags, at);
                    continue;
                }
                // A gem skill cast at the ground (Fireball) stops the player where it stands; anything else (Move) walks there.
                if (InTown(conn) && SkillsFor(conn).ActionName(use.SkillId) != "Move") continue;
                if (SkillsFor(conn).ActionName(use.SkillId) != "Move")
                    await EndGracePeriod(conn, action: true);
                Mark("grace");
                bool cast = SkillsFor(conn).IsGemSkill(use.SkillId);
                var gem = cast ? GemSkillEffects(conn).FirstOrDefault(g => g.Id == use.SkillId) : default;
                if (cast && gem.Name is null)
                {
                    Console.WriteLine($"[gateway] {conn.Remote} skill 0x{use.SkillId:x4}: unknown dynamic skill id (action not started)");
                    continue;
                }
                Mark("gem");
                if (cast && gem.Name is not null && !ProjectilePathAvailable(conn, use.SkillId, gem.Effect)) continue;
                Mark("path");
                // A disabled gem's skill is not confirmed: the client animates the cast and sends the use anyway (it only reds
                // the gem), so without the 0x1ab the action does not run. No projectile, mana or cooldown either.
                if (cast && gem.Name is not null
                    && GemSkillInstanceDisabled(conn, gem.Effect, use.SkillId) is { } disabled)
                {
                    Console.WriteLine($"[gateway] {conn.Remote} skill 0x{use.SkillId:x4} {gem.Name}: {disabled} (disabled, action not started)");
                    // The client paid the cast before the server heard of it: the pool again gives the cost back.
                    await SendPlayerMana(conn, $"0x01b1 player mana {conn.PlayerMana} (disabled skill, cost back)");
                    continue;
                }
                Mark("disabled");
                // The ordinary ranged attack fires at the ground point; it is neither a melee hit nor a Move action.
                // Both bows and wands create their native Projectiles.dat object at the weapon's attack contact.
                string? ordinaryProjectile = OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(conn, activeSet: true));
                if (use.SkillId == SkillCatalog.FirstSkillId && ordinaryProjectile is not null)
                {
                    double duration = WeaponFor(conn).AttackSeconds(CharacterPlayerStats(conn));
                    if (!TryBeginRangedAttack(conn, at, duration, use.SkillId, use.Slot, out var shot)) continue;
                    int projectileEpoch;
                    lock (conn.Monsters) projectileEpoch = conn.ProjectileEpoch;
                    // Release work must not block serial input dispatch: resume/move can invalidate a queued echo.
                    _ = ConfirmAndLaunchOrdinaryRanged(conn,
                        [InstanceProtocol.ActionStarted(PlayerId, shot.X, shot.Y, use.X, use.Y, use.SkillId, use.Slot, use.Flags), InstanceProtocol.ActionEnded(PlayerId)],
                        $"0x01ab ranged attack ({ordinaryProjectile}) at ({use.X}, {use.Y}) from ({shot.X}, {shot.Y}) (t={at:0.000})",
                        (use.X, use.Y), duration, shot.Action, projectileEpoch, ordinaryProjectile, use.Slot, shot.CriticalRoll);
                    continue;
                }
                Mark("ordinary");
                // An attack gem (Holy Strike, Ground Slam …) used on the ground with "Always Attack Without Moving" off: a
                // point beyond the weapon's reach is an attack-move (the client walks to it itself and sends no C→S 0x25
                // Move for that walk, so the server walks its own estimate after it), which costs no mana; a point within
                // reach is a swing at the ground that costs the skill's mana even when nothing stands there (live
                // 2026-10-26: standing and swinging with Holy Strike spent no mana, because every empty point was walked).
                // Before 2026-10-25 every use swung, which paid the cost at the click and echoed the server's stale
                // position, and the client snapped the player back onto it (live trace: up to 98 cells).
                // A ranged weapon (a bow) fires at the point instead: arrows flew nowhere while this branch walked the
                // player, since the projectile is launched by the cast below (live 2026-10-25).
                bool attack = cast && gem.Name is not null && SkillsFor(conn).IsAttack(gem.Effect);
                if (attack && (use.Flags & 0x1) == 0 && !SkillsFor(conn).IsMovement(gem.Effect)
                    && (!SkillsFor(conn).IsProjectile(gem.Effect) || GatewayProjectileSkillProfiles.IsMoltenStrike(SkillsFor(conn).EffectId(gem.Effect)))
                    && WeaponProfileFor(conn).Melee)
                {
                    var here = conn.Walk.Position(at);
                    if (GroundAttackWalks(here, (use.X, use.Y), WeaponFor(conn).Range))
                    {
                        int walkAction = 0;
                        double arrive = at;
                        // A predictive client's action walks up to the weapon's range less one of the aim and swings there
                        // by itself (07 §5), so the estimate stops there too and swings on arrival (SwingAtGroundOnArrival).
                        if (!TryBeginSkillCooldownUse(conn, use.SkillId, gem.Effect,
                            CooldownForSkill(conn, use.SkillId), false,
                            () =>
                            {
                                at = Math.Max(at, ProjectileWorldTime(conn));
                                CancelMeleeRepeats(conn, at);
                                walkAction = Interlocked.Increment(ref conn.ActionCount);
                                if (conn.LockstepClient) conn.Walk.Order(use.X, use.Y, at);
                                else arrive = conn.Walk.Approach(use.X, use.Y, WalkUpRange(WeaponFor(conn)), at).At;
                                conn.ChannelSkill = 0;
                            },
                            out var approachTicket, reserve: false, requestedAt: at)) continue;
                        await SendWorldRecord(conn, PlayerId, InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, use.X, use.Y, use.SkillId, use.Slot, use.Flags),
                            $"0x01ab {gem.Name}: attack-move to ({use.X}, {use.Y}) from ({here.X}, {here.Y}) (t={at:0.000})",
                            still: () => approachTicket?.Current(conn) ?? PlayerActionCurrent(conn, walkAction));
                        // The client spends the attack skill's mana when it starts a use, even a use that only walks (probe
                        // 2026-10-26: 6 mana per attack time while attack-moving), and the server pays only for a swing: the
                        // pool gives the cost back, or the client's orb falls behind and its flasks are refused as "full".
                        // The client repeats this packet every frame while the button is held, so at most four a second.
                        if (conn.Clock.Elapsed.TotalSeconds - conn.LastManaResyncAt >= 0.25)
                        {
                            conn.LastManaResyncAt = conn.Clock.Elapsed.TotalSeconds;
                            await SendPlayerMana(conn,
                                $"0x01b1 player mana {conn.PlayerMana} (attack-move, cost back)");
                        }
                        if (!conn.LockstepClient)
                            _ = SwingAtGroundOnArrival(conn, use, gem, walkAction, arrive);
                        continue;
                    }
                }
                if (cast && gem.Name is not null && IsDashEffect(SkillsFor(conn).EffectId(gem.Effect)))
                {
                    bool valid;
                    lock (conn.Monsters) valid = TryDashLanding(conn, gem.Effect, (use.X, use.Y), out _, out _,
                        skill: use.SkillId, flags: use.Flags);
                    if (!valid)
                    {
                        Console.WriteLine($"[gateway] {conn.Remote} skill 0x{use.SkillId:x4} {gem.Name}: invalid Dash location " +
                            $"({use.X}, {use.Y}) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
                        continue;
                    }
                }
                Mark("attackmove");
                // An attack gem swings with the weapon's attack time, while the client keeps aiming it for as long as the
                // button is held. A use before the next attack is not a new action: it must not cancel the swing in progress
                // (its pending hit), pay its cost or get a 0x1ab (live 2026-10-25).
                bool leapSkill = attack && !IsTotemSkill(conn, use.SkillId) && IsLeapSlam(conn, gem.Effect);
                if (attack && !leapSkill && !AttackReady(conn, use.SkillId, gem.Effect, gem.Level, out double attackSeconds))
                {
                    Console.WriteLine($"[gateway] {conn.Remote} skill 0x{use.SkillId:x4} {gem.Name}: ignored, the previous attack is still running (attack time {attackSeconds:0.00} s) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
                    continue;
                }
                if (cast && gem.Name is not null && IsDashEffect(SkillsFor(conn).EffectId(gem.Effect)))
                {
                    if (!TryCommitDashUse(conn, use.SkillId, gem.Effect, (use.X, use.Y), out var dashUse, at, flags: use.Flags))
                    {
                        Console.WriteLine($"[gateway] {conn.Remote} skill 0x{use.SkillId:x4} {gem.Name}: Dash refused before action admission " +
                            $"at ({use.X}, {use.Y}) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
                        continue;
                    }
                    if (!await CastGemSkill(conn, use.SkillId, dashUse!.From, (use.X, use.Y),
                            sequence: use.Slot, dashReceipt: dashUse)) continue;
                    bool dashAcknowledged = false;
                    await Send(conn,
                        [.. InstanceProtocol.ActionStarted(PlayerId, dashUse.From.X, dashUse.From.Y, use.X, use.Y,
                            use.SkillId, use.Slot, use.Flags), .. InstanceProtocol.ActionEnded(PlayerId)],
                        $"0x01ab player skill 0x{use.SkillId:x4} {gem.Name}: accepted Dash from ({dashUse.From.X}, {dashUse.From.Y}) " +
                        $"to ({dashUse.To.X}, {dashUse.To.Y}) (t={dashUse.AcceptedAt:0.000})",
                        still: () => dashAcknowledged = dashUse.ConfirmNativeUse(conn));
                    if (!dashAcknowledged) continue;
                    await SendNativeCooldownRefresh(conn);
                    ResyncAfter(conn, MovementSeconds(conn, gem.Effect, gem.Level), gem.Name);
                    continue;
                }
                Mark("ready");
                int action = 0;
                (int px, int py) = (0, 0);
                CooldownUseTicket? cooldownUse = null;
                int chargeBonus = cast && gem.Name is not null ? ShieldChargeBonusPct(conn, use.SkillId, gem.Effect) : 0, chargeToken = 0;
                // A lockstep client's forced stationary default attack swings where the player stands. Ordinary ground
                // melee uses take StartLockstepGroundMelee's frame-stepped walk-up before reaching this handler.
                bool inPlace = cast || conn.LockstepClient && use.SkillId == SkillCatalog.FirstSkillId;
                // Flicker Strike on the ground goes to the monster in range nearest to the point (FlickerStrike), and is
                // refused when none is; the server teleports the player, which the client does not.
                bool flicker = cast && gem.Name is not null && SkillsFor(conn).ActionHash(gem.Effect) == FlickerStrike.Action;
                (uint Id, (int X, int Y) At, (int X, int Y) Landing)? flickerTo = null;
                LeapSlam.Use? leap = null;
                void StartAction()
                {
                    at = Math.Max(at, conn.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
                    CancelMeleeRepeats(conn, at);
                    action = Interlocked.Increment(ref conn.ActionCount);
                    (px, py) = inPlace ? conn.Walk.Position(at) : conn.Walk.Order(use.X, use.Y, at);
                    if (inPlace) conn.Walk.Order(px, py, at);
                    // Readiness and reservation precede movement: a raw early Dash cannot teleport anyway. Shield Charge walks
                    // there at its own speed instead (ShieldCharge.cs).
                    if (leap is not null) CommitLeapSlam(conn, leap);
                    else if (chargeBonus != 0) chargeToken = BeginShieldCharge(conn, use.SkillId, chargeBonus, use.X, use.Y, at);
                    else if (flicker && flickerTo is { } to) conn.Walk.Teleport(to.Landing.X, to.Landing.Y, at);
                    else if (gem.Name is not null && !IsDashEffect(SkillsFor(conn).EffectId(gem.Effect)) && LandsAtAim(conn, gem.Effect))
                    {
                        var landing = MovementLanding(conn, gem.Effect, (px, py), (use.X, use.Y), use.SkillId, use.Flags);
                        conn.Walk.Teleport(landing.X, landing.Y, at);
                    }
                    conn.ChannelSkill = cast ? use.SkillId : (ushort)0;
                    conn.ChannelSkillSequence = use.Slot;
                }
                // With no monster in range, Flicker Strike is not used: no cost, cooldown use or charge.
                bool noFlickerTarget = false;
                bool FlickerAdmit()
                {
                    double t = Math.Max(at, conn.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
                    flickerTo = FlickerDestination(conn, gem.Effect, use.SkillId, conn.Walk.Position(t), (use.X, use.Y), 0, t);
                    noFlickerTarget = flickerTo is null;
                    return !noFlickerTarget;
                }
                if (leapSkill)
                {
                    if (!TryBeginAttackUse(conn, use.SkillId, gem.Effect, at,
                        acceptedAt => { at = acceptedAt; StartAction(); }, out cooldownUse,
                        acceptedAt => (leap = PlanLeapSlam(conn, use.SkillId, gem.Effect, (use.X, use.Y), acceptedAt)) is not null))
                        continue;
                }
                else if (cast && gem.Name is not null)
                {
                    if (!TryBeginSkillCooldownUse(conn, use.SkillId, gem.Effect,
                        CooldownForSkill(conn, use.SkillId),
                        false, StartAction, out cooldownUse, requestedAt: at, admit: flicker ? FlickerAdmit : null))
                    {
                        if (noFlickerTarget) await FlickerRefused(conn, gem.Name);
                        continue;
                    }
                }
                else
                {
                    using (ProjectileMutation(conn))
                    {
                        if (PlayerActionBlocked(conn, at, conn.Clock.Elapsed.TotalSeconds)) continue;
                        StartAction();
                        // A lockstep client's default attack at the ground is a swing where it stands (inPlace), which takes
                        // the attack time: the next use waits for it, as after a swing at a monster (Attack).
                        if (inPlace && !cast && use.SkillId == SkillCatalog.FirstSkillId)
                            conn.NextAttackAt = AttackTiming.After(at, conn.NextAttackAt,
                                Math.Max(TickSeconds, WeaponFor(conn).AttackSeconds(CharacterPlayerStats(conn)) / PlayerActionFactor(conn)));
                    }
                }
                Mark("begin");
                if (chargeToken != 0) _ = RunShieldCharge(conn, chargeToken, conn.Walk.ArrivesAt,
                    use.SkillId, use.Slot, cooldownUse?.ActionToken ?? action, cooldownUse);
                Mark("charge");
                bool projectileRuntime = cast && gem.Name is not null && (UsesProjectileRuntime(gem.Effect) || leapSkill);
                double? swingAt = attack ? at : null;
                Task<bool>? castTask = projectileRuntime ? CastGemSkill(conn, use.SkillId,
                    (px, py), (use.X, use.Y), sequence: use.Slot, actionToken: cooldownUse?.ActionToken ?? action, cooldownUse: cooldownUse,
                    swingAt: swingAt, leap: leap) : null;
                if (castTask is { IsCompletedSuccessfully: true, Result: false }) continue;
                Mark("cast");
                bool acknowledged = false;
                bool predictedLeap = cast && PredictsLeapSlam(conn, SkillsFor(conn).EffectId(gem.Effect), use.Slot);
                Func<Task> echo = async () =>
                {
                    await Send(conn, [],
                    $"{(predictedLeap ? "0x01ac predicted Leap Slam acknowledged" : "0x01ab action started")} {use} at ({px}, {py}){(cast ? " (gem skill cast)" : $", walk ends at {conn.Walk.Destination} in {conn.Walk.ArrivesAt - at:0.00} s")} (t={at:0.000})",
                    still: () => acknowledged = SkillAcknowledgementAllowed(conn, gem.Effect, use.SkillId,
                        () => cooldownUse?.CommitNativeUse(conn, use.SkillId) ?? PlayerActionCurrent(conn, action)),
                    atGate: () =>
                    {
                        byte[] reply = [.. InstanceProtocol.ActionStarted(PlayerId, px, py, use.X, use.Y, use.SkillId, use.Slot, use.Flags),
                            .. cast ? InstanceProtocol.ActionEnded(PlayerId) : []];
                        return predictedLeap ? LeapSlamAcknowledgement(conn.Wire.Ciphers, reply) : reply;
                    });
                    if (acknowledged) await SendNativeCooldownRefresh(conn);
                    else if (ServerTrace.Latency)
                        Console.WriteLine($"[gateway] {conn.Remote} action echo refused 0x{use.SkillId:x4}:"
                            + $" ActionCount={conn.ActionCount} action={action} life={conn.PlayerLife} closed={conn.Closed}");
                };
                // The action acknowledgement is sent here and not queued behind the projectile presentation: it is a
                // state reply that the client needs within this use, and the queue's own records (creates, moves, bursts)
                // took long enough that the next click had already started a new action, so the echo was refused as stale
                // and the client never ran the action (live 2026-11-17: 51 of 59 Kinetic Blast casts, no casting animation).
                await echo();
                Mark("echo");
                if (!projectileRuntime && !acknowledged) continue;
                if (cooldownUse?.Current(conn) == false) continue;
                if (flickerTo is { } flickerDestination)
                {
                    await FlickerArt(conn, gem.Gem, (px, py), flickerDestination.Landing);
                    await SendWorldRecord(conn, PlayerId, InstanceProtocol.Teleport(PlayerId, flickerDestination.Landing.X, flickerDestination.Landing.Y),
                        $"0x01b7 {gem.Name} to ({flickerDestination.Landing.X}, {flickerDestination.Landing.Y}) for monster 0x{flickerDestination.Id:x} "
                        + $"at ({flickerDestination.At.X}, {flickerDestination.At.Y}) (t={at:0.000})");
                }
                if (flicker) await GrantFlickerSpeed(conn, use.SkillId);
                // Flicker Strike strikes from where it lands, at the monster it went to.
                var (strikeFrom, strikeAt) = flickerTo is { } struck ? (struck.Landing, struck.At) : ((px, py), (use.X, use.Y));
                if (cast && !projectileRuntime) _ = CastGemSkill(conn, use.SkillId, strikeFrom, strikeAt, targetId: flickerTo?.Id ?? 0, sequence: use.Slot,
                    actionToken: cooldownUse?.ActionToken ?? action, cooldownUse: cooldownUse, swingAt: swingAt);
                // Reconcile the skills that use the immediate landing estimate once their action has finished.
                if (leap is { } jump)
                    ResyncAfter(conn,
                        Math.Max(0, jump.At + jump.Timing.End - conn.Clock.Elapsed.TotalSeconds), gem.Name!);
                else if (cast && gem.Name is not null && chargeBonus == 0 && LandsAtAim(conn, gem.Effect))
                    ResyncAfter(conn, MovementSeconds(conn, gem.Effect, gem.Level, use.SkillId), gem.Name);
                if (!PlayerActionCurrent(conn, action)) continue;
                string mode = Environment.GetEnvironmentVariable("Game_MOVE_MODE") ?? "echo";
                if (mode == "teleport")
                    await SendWorldRecord(conn, PlayerId, InstanceProtocol.Teleport(PlayerId, use.X, use.Y),
                        $"0x01b7 teleport to ({use.X}, {use.Y})", still: () => PlayerActionCurrent(conn, action));
                else if (mode == "move")
                {
                    float speed = float.TryParse(Environment.GetEnvironmentVariable("Game_MOVE_SPEED"), System.Globalization.CultureInfo.InvariantCulture, out var sp) ? sp : 37f;
                    await SendWorldRecord(conn, PlayerId, InstanceProtocol.MoveTo(PlayerId, use.X, use.Y, speed),
                        $"0x01ba move to ({use.X}, {use.Y}) speed {speed}", still: () => PlayerActionCurrent(conn, action));
                }
                }
                finally
                {
                    Mark("tail");
                    double useTotal = Stopwatch.GetElapsedTime(useStarted).TotalMilliseconds;
                    if (ConnectionPerformance.LagProbe && useTotal > 40)
                        Console.WriteLine($"[slow-use] {conn.Remote} total={useTotal:0} cpu={(ThreadCpuMilliseconds() - useCpuStarted):0}"
                            + $" {string.Join(" ", phases)}");
                }
            }
            else if (InstanceProtocol.IsLockstepRequest(id))
            {
                // The client chose lockstep (07 §6): it is served as it asks.
                OnLockstepRequest(conn, id, bytes);
                if (id == InstanceProtocol.LockstepMoveUpdateId) await AimLockstepChannel(conn);
            }
            else if (id == InstancePause.RequestId)
                await SetPaused(conn, InstancePause.ReadRequest(bytes));
            else if (id == InstanceWaypoints.LabyrinthChosenId && bytes.Length >= InstanceWaypoints.LabyrinthChosenLength)
                await ChooseLabyrinth(conn, bytes[2]);
            else if (id == InstanceWaypoints.AscendancyChosenId && bytes.Length >= InstanceWaypoints.AscendancyChosenLength)
                await ChooseAscendancyClass(conn, bytes[2]);
            else if (id == InstanceWaypoints.DescendancyChosenId && bytes.Length >= InstanceWaypoints.DescendancyChosenLength)
                await ChooseDescendancy(conn, bytes[2]);
            else if (id == InstanceProtocol.UseInstantSkillAtLocationId)
            {
                var (x, y, skill) = InstanceProtocol.ReadUseInstantSkillAtLocation(bytes);
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x0190 instant skill 0x{skill:x4} at ({x}, {y})");
                if (SkillsFor(conn).IsGemSkill(skill)) _ = UseInstantSkillAtLocation(conn, skill, (x, y));
            }
            else if (id == InstanceProtocol.UseInstantSkillId)
            {
                // An instant skill (skill_is_instant: Haste, Righteous Fire) has no action: the walk and a pending hit go on,
                // and the client waits for no echo.
                var (target, skill) = InstanceProtocol.ReadUseInstantSkill(bytes);
                var spot = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x0191 instant skill 0x{skill:x4} on 0x{target:x} at ({spot.X}, {spot.Y})");
                if (SkillsFor(conn).IsGemSkill(skill)) _ = UseInstantSkill(conn, skill, target, spot);
            }
            else if (id == InstanceTransfer.UseSkillOnObjectId)
            {
                var use = InstanceTransfer.ReadUseSkillOnObject(bytes);
                // In the background: the player may first walk to the target, and the read loop must keep answering ticks.
                _ = OnUseObject(conn, use, conn.ClockAt(readAt));
            }
            else if (id == InstanceProtocol.BindSkillId)
            {
                var (slot, skill) = InstanceProtocol.ReadBindSkill(bytes);
                string? entry = SkillBarEntry(conn, skill);
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x004e bind skill 0x{skill:x4} ({(entry is null ? "unknown, not saved" : entry == "" ? "empty" : entry)}) to slot {slot}: {Convert.ToHexString(bytes)}");
                if (entry is not null && slot < InstanceProtocol.SkillBarSlots)
                    lock (conn.Monsters) SkillBarLayout.Bind(CharacterName(conn), slot, entry);
            }
            else if (id is InstanceProtocol.AttackInPlaceId or InstanceProtocol.SlotMask52Id)
            {
                // A slot option the client already applied (its bit of instance +0xcfd0 / +0xcfd2); saved so the next
                // 0x53 sends it back instead of clearing it.
                var (slot, on) = InstanceProtocol.ReadSlotOption(bytes);
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x{id:x4} {(id == InstanceProtocol.AttackInPlaceId ? "always attack without moving" : "slot option 0x52")} slot {slot} {(on ? "on" : "off")}");
                if (slot < 16) CharacterSaves.SetSkillBarOption(CharacterName(conn), id == InstanceProtocol.AttackInPlaceId, slot, on);
            }
            else if (id == InstanceEntities.AllocatePassiveId)
            {
                var (u2, node, u6) = InstanceEntities.ReadAllocatePassive(bytes);
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x0034 allocate passive {node} ({PassivesFor(conn).Find(node)?.Name ?? "unknown"}), " +
                    $"node high16 {u2}, expansion {u6}");
                if (u2 == 0 && u6 != 0)
                    await ChangeClusterPassive(conn, node, allocate: true);
                else if (u2 == 0)
                {
                    string? error = await ChangePassives(conn, [node], allocate: true, "Passive Skill Tree");
                    if (error is not null) await conn.Commands!.Reply(error);
                }
                else
                {
                    Console.WriteLine($"[gateway] {conn.Remote} allocation refused: 32-bit passive ids are not supported");
                    await SendPassives(conn);
                }
            }
            else if (id == InstanceEntities.MasteryEffectId)
            {
                var (node, effect) = InstanceEntities.ReadMasteryEffect(bytes);
                await ChangePassives(conn, [node], allocate: true, "Passive Skill Tree mastery", effect);
            }
            else if (id == InstanceEntities.RefundPassiveId)
            {
                var (node, expansion, gold) = InstanceEntities.ReadRefundPassive(bytes);
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x0036 refund passive {node} ({(node <= ushort.MaxValue ? PassivesFor(conn).Find((ushort)node)?.Name : null) ?? "unknown"})" +
                    $"{(expansion ? ", expansion node" : "")}, {(gold ? "gold respec" : "refund points")}: {Convert.ToHexString(bytes)}");
                if (expansion && node <= ushort.MaxValue)
                    await ChangeClusterPassive(conn, (ushort)node, allocate: false);
                else if (!expansion && node <= ushort.MaxValue)
                {
                    if (gold)
                    {
                        string? error = await ChangePassives(conn, [(ushort)node], allocate: false, "Respecialisation", goldRespec: true);
                        if (error is not null) await conn.Commands!.Reply(error);
                    }
                    else
                    {
                        string? error = await ChangePassives(conn, [(ushort)node], allocate: false, "Refund points", refundPoints: true);
                        if (error is not null) await conn.Commands!.Reply(error);
                    }
                }
                else
                {
                    Console.WriteLine($"[gateway] {conn.Remote} refund refused: 32-bit passive ids are not supported");
                    await SendPassives(conn);
                }
            }
            else if (id == InstanceEntities.RefundAllPassivesId)
            {
                // "Refund All" (after the client's Yes): every passive but the ascendancy ones, for their full gold cost.
                var nodes = PassiveState(conn).Nodes.Where(n => PassivesFor(conn).Find(n) is not { IsAscendancy: true }).ToList();
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x0037 refund all passives ({nodes.Count})");
                await ChangePassives(conn, nodes, allocate: false, "Respecialisation, Refund All", goldRespec: true);
            }
            else if (id == InstanceEntities.PanelToggleId)
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x0123 panel {(bytes[2] != 0 ? "opened" : "closed")}");
            else if (id == InstanceItems.DropItemId)
                await DropItem(conn);
            else if (id == InstanceItems.CommandId)
            {
                var command = InstanceItems.ReadCommand(bytes);
                if (command.Id == InstanceItems.DestroyCursorItemCommand) await DestroyHeldItem(conn);
                else if (command.Id == InstanceItems.WarpToHideoutCommand) await WarpToHideout(conn, "/hideout");
                else if (command.Id == InstanceItems.ForceResyncCommand) await SendResync(conn, "the client asked");
                else if (command.Id == InstanceItems.AfkCommand && conn.LockstepClient) AnswerLockstepProbe(conn, readAt);
                else Console.WriteLine($"[gateway] {conn.Remote} <= 0x0016 command {command} (not handled)");
            }
            else if (id == InstanceWaypoints.VisitHideoutId)
            {
                var visit = InstanceWaypoints.ReadVisitHideout(bytes);
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x017a visit the hideout of {visit.Character} (from object 0x{visit.Waypoint:x}, u8 {visit.Flag})");
                // Only the player's own hideout exists (no parties or other players' hideouts yet).
                await WarpToHideout(conn, "World panel Hideout button");
            }
            else if (id == InstanceWaypoints.VisitMenagerieId)
            {
                // The World panel's Menagerie pin: the same layout as 0x17a (whose Menagerie, waypoint object, u8).
                var visit = InstanceWaypoints.ReadVisitHideout(bytes);
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x017b visit the Menagerie of {visit.Character} (from object 0x{visit.Waypoint:x}, u8 {visit.Flag})");
                if (AreaTransitionCatalog.HashOf(WaypointCatalog.MenagerieHub) is { } menagerie)
                    await ChangeArea(conn, new PendingTransfer(menagerie, 0, false, AtSpawn: true), "World panel Menagerie pin");
            }
            else if (id == InstanceWaypoints.VisitChayulaHubId)
            {
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x0182 World panel Monastery of the Keepers pin");
                if (AreaTransitionCatalog.HashOf(WaypointCatalog.ChayulaHub) is { } monastery)
                    await ChangeArea(conn, new PendingTransfer(monastery, 0, false, AtSpawn: true), "World panel Monastery of the Keepers pin");
            }
            else if (id == InstanceWaypoints.VisitHeistHubId)
            {
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x0184 World panel Rogue Harbour pin");
                if (AreaTransitionCatalog.HashOf(WaypointCatalog.HeistHub) is { } harbour)
                    await ChangeArea(conn, new PendingTransfer(harbour, 0, false, AtSpawn: true), "World panel Rogue Harbour pin");
            }
            else if (id == InstanceWaypoints.VisitKingsmarchId)
            {
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x0181 World panel Kingsmarch pin");
                if (AreaTransitionCatalog.HashOf(WaypointCatalog.Kingsmarch) is { } kingsmarch)
                    await ChangeArea(conn, new PendingTransfer(kingsmarch, 0, false, AtSpawn: true), "World panel Kingsmarch pin");
            }
            else if (id == InstanceWaypoints.VisitSanctumId)
            {
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x017e World panel Forbidden Sanctum pin");
                if (AreaTransitionCatalog.HashOf(WaypointCatalog.SanctumHub) is { } sanctum)
                    await ChangeArea(conn, new PendingTransfer(sanctum, 0, false, AtSpawn: true), "World panel Forbidden Sanctum pin");
            }
            else if (id == InstanceWaypoints.VisitDelveHubId)
            {
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x017d World panel Mine Encampment pin");
                if (AreaTransitionCatalog.HashOf(WaypointCatalog.DelveHub) is { } delve)
                    await ChangeArea(conn, new PendingTransfer(delve, 0, false, AtSpawn: true), "World panel Mine Encampment pin");
            }
            else if (id == InstanceItems.UnsocketGemId)
                await UnsocketGem(conn, InstanceItems.ReadUnsocketGem(bytes));
            else if (id == InstanceItems.SocketGemId)
                await SocketGem(conn, InstanceItems.ReadSocketGem(bytes));
            else if (id == InstanceItems.LevelUpGemId)
                await LevelUpGem(conn, InstanceItems.ReadLevelUpGem(bytes));
            else if (id == InstanceItems.LevelUpAllGemsId)
                await LevelUpAllGems(conn);
            else if (id == InstanceItems.LockGemId)
                await LockGem(conn, InstanceItems.ReadLockGem(bytes));
            else if (id == InstanceItems.PlaceItemId)
                await PlaceItem(conn, InstanceItems.ReadPlaceItem(bytes));
            else if (id == InstanceItems.ItemActionId)
                await HeldOntoItem(conn, InstanceItems.ReadItemAction(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceItems.SplitStackId)
                await SplitStack(conn, InstanceItems.ReadSplitStack(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceItems.UseItemId)
                await UseItem(conn, InstanceItems.ReadUseItem(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceItems.UseFlaskId)
                await UseFlask(conn, InstanceItems.ReadUseFlask(bytes));
            else if (id == InstanceItems.UseItemOnId)
                await UseItemOn(conn, InstanceItems.ReadUseItemOn(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceItems.UseItemTypeOnId)
            {
                // Shift held: the currency stays on the cursor and is named by type; use a stack of it from the inventories,
                // the main inventory first.
                var again = InstanceItems.ReadUseItemTypeOn(bytes);
                var items = ItemsOf(conn);
                byte[] rows = [PlayerInventories.MainInventory, .. Enumerable.Range(0, InstanceInventories.PlayerDefaults.Length).Select(r => (byte)r)];
                var stack = rows.Select(r => (Row: r, Item: items.Items(r).FirstOrDefault(i => i.Item.TypeHash == again.TypeHash))).FirstOrDefault(s => s.Item is not null);
                // None in the inventories: the currency was taken from the open stash, so use a stack of it from a stash tab.
                var stash = StashOf(conn);
                (int Tab, InventoryItem Item)? inStash = null;
                for (int t = 0; stack.Item is null && inStash is null && t < stash.Count; t++)
                    if (stash.Inventory(t).Items.FirstOrDefault(i => i.Item.TypeHash == again.TypeHash) is { } found) inStash = (t, found);
                if (inStash is var (stashTab, stashItem))
                    await UseItemOn(conn, new InstanceItems.UseItemOn(stash.InventoryOf(stashTab), stashItem.Id,
                        again.TargetInventoryId, again.TargetId), Convert.ToHexString(bytes));
                else if (stack.Item is null) Console.WriteLine($"[gateway] {conn.Remote} <= 0x0072 use type 0x{again.TypeHash:x8} again: none left");
                else await UseItemOn(conn, new InstanceItems.UseItemOn(PlayerInventories.InventoryId(stack.Row), stack.Item.Id,
                    again.TargetInventoryId, again.TargetId), Convert.ToHexString(bytes));
            }
            else if (id == InstanceItems.InventoryRequestId)
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x{id:x4} (inventory request, not handled yet): {Convert.ToHexString(bytes)}");
            else if (id == InstanceItems.PickUpItemId)
                await PickUpItem(conn, InstanceItems.ReadPickUpItem(bytes));
            else if (id == InstanceProtocol.ExitToCharacterSelectId)
            {
                // Esc → "Exit to Character Selection" or the hardcore death screen's Exit. Closing the instance connection
                // is the answer: the client keeps its login connection and goes back to the character list (09 §6).
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x0153 exit to character selection: closing the instance connection");
                conn.CloseReason = "exit to character selection (0x0153)";
                lock (conn.Monsters)
                {
                    conn.Closed = true;
                    conn.InvalidateIncomingHits();
                }
                return;
            }
            else if (id == InstanceProtocol.ResurrectId)
                _ = Resurrect(conn, InstanceProtocol.ReadResurrectInTown(bytes));
            else if (id == InstanceNpcs.DialogueClosedId)
                await CloseNpcDialogue(conn);
            else if (id == InstanceStash.RequestTabId)
            {
                var (tab, kind) = InstanceStash.ReadRequestTab(bytes);
                await SendStashTab(conn, tab, kind);
            }
            else if (id == InstanceStash.ReorderTabsId)
            {
                // The tab buttons' order, kept with the stash (personal or guild by kind) and sent back in the tab lists.
                var (kind, order) = InstanceStash.ReadReorderTabs(bytes);
                if (kind == InstanceStash.Guild && conn.Ssf) continue;
                var target = kind == InstanceStash.Guild ? GuildStashOf(conn) : PersonalStashOf(conn);
                int moved = target.SetPositions(order);
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x00cc stash {kind} tab order: {string.Join(", ", order.Select(o => $"{o.Index}->{o.Position}"))} ({moved} moved, saved)");
            }
            else if (InstanceProtocol.IsChannelUpdate(id))
            {
                // A channelled skill (Cyclone, Winter Orb) goes on, aimed at this cell: Cyclone walks toward it
                // (ChannelAimedAt). Its hits run on their own timer (RunChannel), at the skill's attack rate, wherever the
                // player is. Without this update the channel counts as silent and ends (ChannelSilenceSeconds).
                var (cx, cy) = InstanceProtocol.ReadChannelUpdate(bytes);
                double t = conn.ClockAt(readAt);
                using (ProjectileMutation(conn))
                {
                    t = Math.Max(t, conn.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
                    if (PlayerActionBlocked(conn, t, conn.Clock.Elapsed.TotalSeconds)) continue;
                    ChannelAimedAt(conn, cx, cy, t);
                    conn.LastChannelUpdateAt = conn.Clock.Elapsed.TotalSeconds;
                }
            }
            else if (id == InstanceProtocol.ChannelEndId)
                ChannelReleased(conn, conn.ClockAt(readAt));
            else if (id == InstanceProtocol.SwapWeaponsId)
            {
                // The weapon swap: the hands' rows and the other set's trade their items (the client shows the active set in
                // the hands' rows), each of the four inventories gets its new contents (0xbf), S→C 0xde gives the set (which
                // also ends the client's wait), then what the hands hold, with the new set's stats and skills (13-items.md).
                var swapItems = ItemsOf(conn);
                byte wanted = (byte)(bytes[2] == 1 ? 1 : 0);
                byte finalWeaponSet;
                List<(byte[] Packet, string Label)> swapOutputs = [];
                // The current-set decision, mutation, qualification and final-set capture share one boundary. Outputs
                // and SendEquipment may suspend after it; the 0xde set below is the one committed by this message.
                using (ProjectileMutation(conn, advance: false, phase: "weapon swap"))
                {
                    finalWeaponSet = swapItems.WeaponSet;
                    if (wanted != swapItems.WeaponSet)
                    {
                        var sourcesBefore = PlayerSkillSourcesLocked(conn);
                        var before = new[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1, PlayerInventories.Weapon2, PlayerInventories.Offhand2 }
                            .ToDictionary(r => r, r => swapItems.Items(r).ToArray());
                        swapItems.SwapWeaponSets();
                        swapItems.WeaponSet = wanted;
                        finalWeaponSet = wanted;
                        using (new PlayerStatScope(conn, fresh: true))
                        {
                            RecomputePoolsAndSpeed(conn);
                            ReconcileBeastCompanionsLocked(conn);
                            ReconcileEquipmentSourceLifetimesLocked(conn, sourcesBefore);
                        }
                        foreach (var (row, old) in before)
                        {
                            uint inv = PlayerInventories.InventoryId(row);
                            var now = swapItems.Items(row).ToArray();
                            if (old.Length + now.Length == 0) continue;
                            swapOutputs.Add((InstanceItems.InventoryChange(inv, swapItems.NextRevision(row),
                                [.. old.Select(i => i.Id)], now),
                                $"0x00bf inventory {inv}: weapon swap, {old.Length} out, {now.Length} in"));
                        }
                    }
                }
                foreach (var (packet, label) in swapOutputs) await Send(conn, packet, label);
                await Send(conn, InstanceProtocol.WeaponSet(finalWeaponSet == 1),
                    $"0x00de weapon set {finalWeaponSet + 1}");
                await SendEquipment(conn, swapItems);
            }
            else if (id == InstanceStash.RenameTabId)
            {
                // A premium tab renamed in the tab settings; kept with the stash (personal or guild), account-wide.
                var (kind, tab, name) = InstanceStash.ReadRenameTab(bytes);
                if (kind == InstanceStash.Guild && conn.Ssf) continue;
                bool done = (kind == InstanceStash.Guild ? GuildStashOf(conn) : PersonalStashOf(conn)).Rename(tab, name);
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x00cd stash {kind} tab {tab} renamed \"{name}\": {(done ? "saved" : "refused")}");
                await SendTabListAgain(conn, kind);
            }
            else if (id == InstanceStash.ColourTabId)
            {
                var (kind, tab, colour) = InstanceStash.ReadColourTab(bytes);
                if (kind == InstanceStash.Guild && conn.Ssf) continue;
                bool done = (kind == InstanceStash.Guild ? GuildStashOf(conn) : PersonalStashOf(conn)).SetColour(tab, colour);
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x00ce stash {kind} tab {tab} colour 0x{colour:x8}: {(done ? "saved" : "refused")} ({Convert.ToHexString(bytes)})");
                await SendTabListAgain(conn, kind);
            }
            else if (id == InstanceStash.MoveToTabId)
                await MoveToStash(conn, InstanceStash.ReadMoveToTab(bytes));
            else if (id == InstanceStash.MoveToAffinityTabId)
                await MoveToAffinityTab(conn, InstanceStash.ReadMoveToAffinityTab(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceStash.SetAffinitiesId)
                await SetTabAffinities(conn, InstanceStash.ReadSetAffinities(bytes));
            else if (id == InstanceStash.TakeFromTabId)
            {
                var move = InstanceStash.ReadTakeFromTab(bytes);
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x00e3 take item {move.ItemId} from stash tab {move.Tab} " +
                    $"({move.Kind:x2} {move.TokenLo:x2} {move.TokenHi:x2} {move.Bulk:x2}, {(move.Bulk != 0 ? "bulk" : "one stack")})");
                await TakeFromStash(conn, move.Tab, move.ItemId, all: move.Bulk != 0);
            }
            else if (id == InstanceStash.MoveTabId)
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x00d1 stash tab into a folder (not handled): {Convert.ToHexString(bytes)}");
            else if (id == InstanceNpcs.OptionId)
                await ChooseNpcOption(conn, InstanceNpcs.ReadOption(bytes));
            else if (id == InstanceHideouts.SelectId)
                await SelectHideout(conn, InstanceHideouts.ReadSelect(bytes));
            else if (id == InstanceChat.ChatId)
            {
                var chat = InstanceChat.ReadChat(bytes);
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x0015 chat \"{chat.Text}\" ({chat.Links.Count} linked)");
                // Plain lines are the Chat module's.
                if (chat.IsCommand)
                {
                    // A preceding slash command can finish a Game + Commands reload in this same batch.
                    // Resolve the current league's builders after that wave, retaining Gateway sinks/mod fallback.
                    // Run this table: an unload while the command runs empties conn.Commands (Program.ForgetCommands).
                    var commands = Program.Commands(conn);
                    conn.Commands = commands;
                    await commands.RunAsync(chat.Text);
                }
            }
            else if (id == InstanceTransfer.ChooseInstanceId)
            {
                byte row = bytes[2];
                if (conn.Chooser is not { } chooser) { Console.WriteLine($"[gateway] {conn.Remote} <= 0x00af row {row} without a list"); continue; }
                conn.Chooser = null;
                bool isNew = row == InstanceTransfer.NewInstanceRow;
                if (!isNew && row >= chooser.Ids.Count) { Console.WriteLine($"[gateway] {conn.Remote} <= 0x00af row {row} out of range"); continue; }
                ulong instance = isNew ? 0 : chooser.Ids[row];
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x00af chose {(isNew ? "a new instance" : $"instance {instance}")} of 0x{chooser.Area:x4}");
                await ChangeArea(conn,
                    new PendingTransfer(chooser.Area, instance, isNew, AtWaypoint: chooser.AtWaypoint)
                    { ArrivalEntrance = chooser.ArrivalEntrance }, $"chooser row {row}");
            }
            else if (id == InstanceWaypoints.TravelId)
                _ = TravelByWaypoint(conn, InstanceWaypoints.ReadTravel(bytes));
            else if (id == InstanceSocial.FriendAddId)
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x0080 add friend \"{InstanceSocial.ReadFriendAdd(bytes).Name}\" (not handled yet)");
            else if (id == InstanceProtocol.AreaGeneratedId)
            {
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x007d area generated: {Convert.ToHexString(bytes)}");
                if (!conn.PlayerSent)
                {
                    // First visit with scripts/walkmaps running: the map is written while the client generates the area.
                    if (conn.Layout.Walkable is null && await AreaLayouts.WaitFor(conn.Layout, conn.Seed) is { } fresh)
                    {
                        conn.Use(fresh);
                        Console.WriteLine($"[gateway] {conn.Remote} walk map captured: {fresh.Describe()}");
                    }
                    if (conn.Layout.AreaTransitions.Length == 0 && await AreaLayouts.WaitForTransitions(conn.Layout, conn.Seed) is { } withTransitions)
                    {
                        conn.Use(withTransitions);
                        Console.WriteLine($"[gateway] {conn.Remote} area transitions captured: {withTransitions.Describe()}");
                    }
                    // Waypoint travel: next to the destination's waypoint instead of the area's spawn. A generated area's
                    // waypoint is only known from the walk-map recorder's capture for this seed (live.ps1 -Walkmaps).
                    // Back through a town portal: where the portal stands (OpenTownPortal).
                    var (arrivalLayout, arrivalNote) = ArrivalLayout(conn);
                    if (!ReferenceEquals(arrivalLayout, conn.Layout)) conn.Use(arrivalLayout);
                    if (arrivalNote is not null) Console.WriteLine($"[gateway] {conn.Remote} {arrivalNote}");
                    Sirus.ConfigureArrival(conn);
                    RecordVisit(conn);
                    // An area with a Trial of Ascendancy gives the Lord's Labyrinth quest, before the player object carries the flags.
                    if (LabyrinthTrials.For(conn.Layout.Id) is not null && !conn.HasQuestFlag(Quests.LabyrinthQuestReceived))
                        await SetQuestFlags(conn, [Quests.LabyrinthQuestReceived]);
                    await SendPlayer(conn);
                    await PlayerEnteredArea(conn);
                    await SendWaypoints(conn);
                    uint gold = PersonalStashOf(conn).Gold;
                    await Send(conn, InstanceEntities.Gold(gold), $"0x02aa gold: {gold} (account, {conn.League ?? "Standard"})");
                    // The worn cosmetics (S→C 0x1f3): the client's worn table starts empty with every instance connection.
                    await SendWornCosmetics(conn, "entering the area");
                    // The saved skill bar (S→C 0x53), right after the player and the area.
                    await SendSkillBar(conn);
                    // The equipment again (S→C 0x02bc), as after an equip: the player object's own list does not set how a
                    // two-handed weapon is held, the update does (live 2026-10-22: it was held one-handed after every load).
                    await SendModel(conn, Looks(conn, ItemsOf(conn)), "entering");
                    // The world runs from here: the client shows the player and its area only once the frames start
                    // (S→C 0x2b8 first), so they start before the monsters are placed, which may take a moment, and the
                    // monsters come in while the player already stands there, as a summon or a pack spawned in a fight.
                    ActivateResourceEffects(conn, ProjectileWorldTime(conn));
                    // Nobody walked while the client was on its loading screen, so the walk history starts here: without this
                    // the first collision pass replayed the whole entry (1.2 s of frames) under the combat lock, 258 ms, and
                    // the player's first movement waited for it (live 2026-11-17).
                    lock (conn.Monsters)
                        conn.WalkersSteppedTo = ProjectileWorldTime(conn);
                    StartWorldFrames(conn);
                    Program.ActivatePlayerUpdates(conn);
                    await PopulateArea(conn);
                    // The map overlay's "X monsters remain" (S→C 0x1a0), once the whole area is placed and long after S→C
                    // 0x21 activated it (SendPlayer, above): a new instance's byte is 0xff, so without this the line
                    // reads "More than 50 monsters remain" everywhere (InstanceProtocol.MonstersRemaining).
                    await SendRemainingMonsters(conn, force: true);
                    await SendGroundItems(conn);
                    await PlaceStartWeapon(conn);
                    await SpawnTownNpcs(conn);
                    await SpawnTownStash(conn);
                    // All stationary objects must exist before validating the arrival cell.
                    await PushOutOfCollision(conn, tell: true);
                    await conn.Commands!.Reply($"Area generated at Seed: {conn.Seed}");
                    // An area the Gateway does not generate itself (ServerLevels) runs on the client's layout alone.
                    if (!ServerLevels.IsSupported(conn.Layout.Id) && !CaptureTour.Active(CharacterName(conn)))
                        await conn.Commands!.Reply(ServerLevels.UnsupportedMessage);
                    await GameEvents.RunAreaGenerated(conn);
                    _ = conn.RampageWorld;
                    lock (conn.Monsters)
                        if (conn.OwnedCombatState is RampageWorld world) EnsureRampageWorker(conn, world);
                }
            }
        }
        // a1q5: after a read that moved items, the Medicine Chest's flag (and its removal once Nessa has one), here on the
        // message path so the inventory revisions stay in order.
        await GameEvents.RunMessagesHandled(conn, [.. messages.Select(m => m.Id)]);
        if (unknown.Length > 0)
        {
            ushort uid = unknown.Length > 1 ? (ushort)(unknown[0] << 8 | unknown[1]) : unknown[0];
            Console.WriteLine($"[gateway] {conn.Remote} <= unframed 0x{uid:x4} ({unknown.Length} bytes): {Convert.ToHexString(unknown.AsSpan(0, Math.Min(unknown.Length, 256)))}");
            // C→S 0x29d: Hinekora's Lock preview request: u16 key, u32 currency inventory, u32 currency id,
            // u32 target inventory, u32 target id (all BE after the key). The 0x2a0 answer uses the same key.
            if (uid == 0x029d && unknown.Length >= 20)
            {
                static uint U32(byte[] b, int i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
                ushort key = (ushort)(unknown[2] << 8 | unknown[3]);
                uint currencyInventory = U32(unknown, 4), currencyId = U32(unknown, 8);
                uint targetInventory = U32(unknown, 12), targetId = U32(unknown, 16);
                var items = ItemsOf(conn);
                var currency = items.RowOf(currencyId) is byte currencyRow
                    ? items.Items(currencyRow).FirstOrDefault(i => i.Id == currencyId) : null;
                var target = items.RowOf(targetId) is byte targetRow
                    ? items.Items(targetRow).FirstOrDefault(i => i.Id == targetId) : null;
                Item? rawPreview = currency is not null && target is not null && target.Item.Foreseeing
                    ? CurrencyResult(conn, target.Item, currency.Item.TypeHash,
                        ItemRolls.ForeseeRandom(target.Item, currency.Item.TypeHash))
                    : null;
                Item? preview = rawPreview is null ? null : ItemRolls.ClearForesee(rawPreview);
                conn.ForesightRequest = (key, currencyInventory, currencyId, targetInventory, targetId,
                    target?.Item.ForeseeSeed ?? 0, preview);
                await Send(conn, InstanceItems.CurrencyApplicationForesightResult(
                    key, preview is null ? InstanceItems.CurrencyApplicationForesightFailure : (ushort)0,
                    preview is null ? [] : [preview]),
                    $"0x02a0 foresight preview key {key} for item {targetId}: {(preview is null ? "refused" : "preview")}");
            }
            // Unframed messages have the hooks too.
            if (GameEvents.WantsPacket(uid)) await GameEvents.AnyPacket(conn, uid, unknown);
        }
    }
}
