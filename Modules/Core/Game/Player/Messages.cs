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
    static readonly string MoveMode = Environment.GetEnvironmentVariable("Game_MOVE_MODE") ?? "echo";
    static readonly float MoveSpeed = float.TryParse(Environment.GetEnvironmentVariable("Game_MOVE_SPEED"),
        System.Globalization.CultureInfo.InvariantCulture, out var sp) ? sp : 37f;

    // Where the player appears in the area, with the line that says why. Back through a town portal: where the portal
    // stands (OpenTownPortal). Waypoint travel: next to the destination's waypoint, which a generated area has only from
    // the walk-map recorder's capture for this seed (live.ps1 -Walkmaps). Otherwise next to the exit back; Resurrect at
    // Checkpoint and the monster packs' clearance use it too. Read when 0x1d goes out (the packs' plan) and at 0x7d.
    static (AreaLayout Layout, string? Note) ArrivalLayout(Player player)
    {
        var layout = player.Layout;
        if (player.ArriveAt is var (sx, sy))
            return (layout with { SpawnX = sx, SpawnY = sy }, $"back at the portal ({sx}, {sy})");
        if (player.ArriveAtWaypoint && layout.Waypoint is { } arrival)
        {
            var (ax, ay) = layout.Near((arrival.GridX, arrival.GridY));
            return (layout with { SpawnX = ax, SpawnY = ay }, $"arrived by waypoint: spawn ({ax}, {ay}) next to the waypoint at ({arrival.GridX}, {arrival.GridY})");
        }
        if (layout.Arrival(player.CameFrom, player.ArrivalEntrance) is var (ex, ey) && (ex, ey) != (layout.SpawnX, layout.SpawnY))
            return (layout with { SpawnX = ex, SpawnY = ey }, $"arrival from 0x{player.CameFrom:x4}: ({ex}, {ey})");
        if (player.ArriveAtWaypoint)
            return (layout, $"arrived by waypoint, but {layout.Id} seed {player.Seed} has no captured waypoint " +
                $"(run live.ps1 -Walkmaps): spawn ({layout.SpawnX}, {layout.SpawnY}){(layout.Walkable is null ? ", no walk map either: the player may stand outside the map" : "")}");
        return (layout, null);
    }

    // How long the hold on the player still has to run, if it is within limit; else 0.
    static double HoldEndsWithin(Player player, double limit)
    {
        double now = AreaTime(player);
        double left = Math.Max(player.HitRecovery.Until, player.ElementalAilments.FreezeUntil(now)) - now;
        return left <= limit ? Math.Max(left, 0) : 0;
    }

    // Temporary lag instrumentation (remove with the investigation): this thread's CPU time tells a slow call from a
    // stall (waiting for a lock or the pool, a collection, the thread descheduled).
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    // Linux: clock_gettime(CLOCK_THREAD_CPUTIME_ID = 3). Elsewhere the probe reports 0 rather than throwing.
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "clock_gettime")]
    static extern int ClockGetTime(int clock, out Timespec time);
    struct Timespec { public long Seconds, Nanoseconds; }
    static long ThreadCpuMilliseconds() =>
        OperatingSystem.IsWindows()
            ? GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? (kernel + user) / 10_000 : 0
            : OperatingSystem.IsLinux() && ClockGetTime(3, out var time) == 0 ? time.Seconds * 1000 + time.Nanoseconds / 1_000_000
            : 0;

    public static async Task OnClientMessages(IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, Player player, long readAt = 0)
    {
        foreach (var (id, bytes) in messages)
        {
            if (player.Closed) return;
            using var timing = player.Connection.Performance.Message(id);
            // One character snapshot for this message: its validation, payment and launch read the same stats instead of
            // rebuilding the whole character five or six times (2026-11-05: 154 builds/s cost a full core).
            using var stats = new PlayerStatScope(player);
            // Temporary lag instrumentation (remove with the investigation): which phase of a cast message takes its time.
            long useStarted = Stopwatch.GetTimestamp(), phaseAt = useStarted, useCpuStarted = ConnectionPerformance.LagProbe ? ThreadCpuMilliseconds() : 0;
            List<string>? phases = ConnectionPerformance.LagProbe ? [] : null;
            void Mark(string name)
            {
                if (phases is null) return;
                long now = Stopwatch.GetTimestamp();
                phases.Add($"{name}={Stopwatch.GetElapsedTime(phaseAt, now).TotalMilliseconds:0}");
                phaseAt = now;
            }
            // A terminal Hardcore owner may only leave for character selection or log in.
            if (HardcoreProgressBlocked(player) && id is not (InstanceProtocol.ExitToCharacterSelectId or InstanceProtocol.ExitToLoginId)
                && !(player.PendingCharacterSelectExit && id == InstanceTransfer.LeaveAreaAckId)) continue;
            // While an area change is in flight (0x2b4 sent, pre-generating the destination before 0x27), ignore
            // gameplay, inventory and interaction packets; only transfer acknowledgement, exit and pause control run.
            if (player.LeavingArea && id is not (InstanceTransfer.LeaveAreaAckId or InstanceProtocol.ExitToCharacterSelectId
                or InstanceProtocol.ExitToLoginId or InstancePause.RequestId)) continue;
            // Let ClientMessage intercept chat and slash commands before ChatModule consumes 0x0015 at ModuleRank.
            if (id == InstanceChat.ChatId && GameEvents.ClientMessage.Any
                && await GameEvents.AnyClientMessage(player, InstanceChat.ReadChat(bytes)))
                continue;
            // A mod may take the message (Program.Mods.cs): then the Gateway's own handler below does not run.
            if (GameEvents.WantsPacket(id) && await GameEvents.AnyPacket(player, id, bytes)) continue;
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
            // The client's answer to 0x2b4 (0x14214efe0): it is ready to leave, so prepare the destination area and move
            // it off the inbox handler so 0x1b ticks keep getting answered while "Entering <area>" is displayed.
            if (id == InstanceTransfer.LeaveAreaAckId)
            {
                if (player.PendingCharacterSelectExit)
                {
                    await FinishCharacterSelectExit(player);
                    return;
                }
                if (player.PendingTransfer is { } leaving) _ = AreaFrames.Start(() => RunTransfer(player, leaving));
                continue;
            }
            // Native TakeHit blocks predicted action starts too. Buffered normal actions must not restart the server's
            // movement/cast timeline during that same recovery. Flasks and instant skills retain their own paths.
            if (id is InstanceProtocol.UseSkillAtLocationId or InstanceTransfer.UseSkillOnObjectId or InstanceProtocol.ChannelUpdateId
                or InstanceProtocol.ChannelUpdateAltId)
            {
                // The client buffers a click from the last part of a hold and sends it as its own hold ends, which can be a
                // few ms before the server's. On localhost it arrives inside them: dropped, the client acts alone.
                // A paused world never reaches the hold's end, and the resume (C→S 0x1d4 0) queues behind this message.
                while (!player.Paused && HoldEndsWithin(player, HoldEndGraceSeconds) is > 0 and var left)
                {
                    await AreaDelay(player, left + .001);
                    readAt = 0;
                }
                if (PlayerActionBlocked(player, player.ClockAt(readAt), AreaTime(player)))
                {
                    // The client starts the newest click when it can again: the server walks it then (ReleaseHeldMove).
                    if (!InstanceProtocol.IsChannelUpdate(id))
                        player.HeldMove = id == InstanceProtocol.UseSkillAtLocationId && InstanceProtocol.ReadUseSkillAtLocation(bytes) is var held
                            && SkillsFor(player).ActionName(held.SkillId) == "Move" ? held : null;
                    continue;
                }
                player.HeldMove = null;
            }
            if (id == InstanceProtocol.UseSkillAtLocationId) Mark("hold");
            if (id == InstanceProtocol.UseSkillAtLocationId)
            {
                try
                {
                // Echo the action so the client runs it (Move on a ground click); an attack gem is paced and may be movement
                // (below). The client starts the action itself and waits for this echo before sending the next one.
                var use = InstanceProtocol.ReadUseSkillAtLocation(bytes);
                double at = player.ClockAt(readAt);   // when the client applied the click, not when this handler runs
                Mark("read");
                if (use.SkillId == TutorialObjects.WashedUpSkillId)
                {
                    await WakeUpOnBeach(player, (use.X, use.Y), use.Slot, use.Flags, at);
                    continue;
                }
                // A gem skill cast at the ground (Fireball) stops the player where it stands; anything else (Move) walks there.
                var skills = SkillsFor(player);
                string? actionName = skills.ActionName(use.SkillId);
                if (InTown(player) && actionName != "Move") continue;
                if (actionName != "Move")
                    await EndGracePeriod(player, action: true);
                Mark("grace");
                bool cast = skills.IsGemSkill(use.SkillId);
                var gem = cast ? GemSkillEffects(player).FirstOrDefault(g => g.Id == use.SkillId) : default;
                if (cast && gem.Name is null)
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{use.SkillId:x4}: unknown dynamic skill id (action not started)");
                    continue;
                }
                Mark("gem");
                if (cast && gem.Name is not null && !ProjectilePathAvailable(player, use.SkillId, gem.Effect)) continue;
                Mark("path");
                // A disabled gem's skill is not confirmed: the client animates the cast and sends the use anyway (it only reds
                // the gem), so without the 0x1ab the action does not run. No projectile, mana or cooldown either.
                if (cast && gem.Name is not null
                    && GemSkillInstanceDisabled(player, gem.Effect, use.SkillId) is { } disabled)
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{use.SkillId:x4} {gem.Name}: {disabled} (disabled, action not started)");
                    // The client paid the cast before the server heard of it: the pool again gives the cost back.
                    await SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana} (disabled skill, cost back)");
                    continue;
                }
                Mark("disabled");
                // The ordinary ranged attack fires at the ground point; it is neither a melee hit nor a Move action.
                // Both bows and wands create their native Projectiles.dat object at the weapon's attack contact.
                if (use.SkillId == SkillCatalog.FirstSkillId
                    && OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(player, activeSet: true)) is { } ordinaryProjectile)
                {
                    double duration = DefaultAttackSeconds(player, use.Slot, CharacterPlayerStats(player));
                    if (!TryBeginRangedAttack(player, at, duration, use.SkillId, use.Slot, out var shot)) continue;
                    NotePlayerAttack(player);
                    int projectileEpoch;
                    projectileEpoch = player.ProjectileEpoch;
                    // Release work must not block serial input dispatch: resume/move can invalidate a queued echo.
                    _ = ConfirmAndLaunchOrdinaryRanged(player,
                        [InstanceProtocol.ActionStarted(player.Id, shot.X, shot.Y, use.X, use.Y, use.SkillId, use.Slot, use.Flags), InstanceProtocol.ActionEnded(player.Id)],
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
                // An attack thrown as a trap or mine is a throw, not a swing: it takes the throwing time and is paced by
                // the placement itself. Gated on the bow's attack time, every third held throw was ignored (live 2026-11-19:
                // Toxic Rain with Trap Support, throws 0.385 s apart against a 0.45 s attack).
                bool attack = cast && gem.Name is not null && SkillsFor(player).IsAttack(gem.Effect)
                    && PlacedKind(SupportedSkill(player, use.SkillId)) is { Trap: false, Mine: false };
                if (attack && (use.Flags & 0x1) == 0 && AttackUsesWalkUp(player, gem.Effect)
                    && (!SkillsFor(player).IsProjectile(gem.Effect) || GatewayProjectileSkillProfiles.IsMoltenStrike(SkillsFor(player).EffectId(gem.Effect)))
                    && WeaponProfileFor(player).Melee)
                {
                    var here = player.Walk.Position(at);
                    int swingReach = MeleeSkillReach(player, WeaponFor(player), gem.Effect, SupportedSkill(player, use.SkillId).Stats);
                    if (GroundAttackWalks(here, (use.X, use.Y), swingReach))
                    {
                        int walkAction = 0;
                        double arrive = at;
                        // A predictive client's action walks up to the weapon's range less one of the aim and swings there
                        // by itself (07 §5), so the estimate stops there too and swings on arrival (SwingAtGroundOnArrival).
                        if (!TryBeginSkillCooldownUse(player, use.SkillId, gem.Effect,
                            CooldownForSkill(player, use.SkillId), false,
                            () =>
                            {
                                at = Math.Max(at, ProjectileWorldTime(player));
                                CancelMeleeRepeats(player, at);
                                walkAction = Interlocked.Increment(ref player.ActionCount);
                                if (player.Connection.LockstepClient) player.Walk.Order(use.X, use.Y, at);
                                else arrive = player.Walk.Approach(use.X, use.Y, Math.Max(swingReach, 1) - 1, at).At;
                                player.ChannelSkill = 0;
                            },
                            out var approachTicket, reserve: false, requestedAt: at)) continue;
                        await SendWorldRecord(player, player.Id, InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, use.X, use.Y, use.SkillId, use.Slot, use.Flags),
                            $"0x01ab {gem.Name}: attack-move to ({use.X}, {use.Y}) from ({here.X}, {here.Y}) (t={at:0.000})",
                            still: () => approachTicket?.Current(player) ?? PlayerActionCurrent(player, walkAction));
                        // The client spends the attack skill's mana when it starts a use, even a use that only walks (probe
                        // 2026-10-26: 6 mana per attack time while attack-moving), and the server pays only for a swing: the
                        // pool gives the cost back, or the client's orb falls behind and its flasks are refused as "full".
                        // The client repeats this packet every frame while the button is held, so at most four a second.
                        if (AreaTime(player) - player.LastManaResyncAt >= 0.25)
                        {
                            player.LastManaResyncAt = AreaTime(player);
                            await SendPlayerMana(player,
                                $"0x01b1 player mana {player.PlayerMana} (attack-move, cost back)");
                        }
                        if (!player.Connection.LockstepClient)
                            _ = SwingAtGroundOnArrival(player, use, gem, walkAction, arrive);
                        continue;
                    }
                }
                if (cast && gem.Name is not null && IsDashEffect(SkillsFor(player).EffectId(gem.Effect)))
                {
                    bool valid;
                    valid = TryDashLanding(player, gem.Effect, (use.X, use.Y), out _, out _,
                        skill: use.SkillId, flags: use.Flags);
                    if (!valid)
                    {
                        Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{use.SkillId:x4} {gem.Name}: invalid Dash location " +
                            $"({use.X}, {use.Y}) (t={AreaTime(player):0.000})");
                        continue;
                    }
                }
                Mark("attackmove");
                // An attack gem swings with the weapon's attack time, while the client keeps aiming it for as long as the
                // button is held. A use before the next attack is not a new action: it must not cancel the swing in progress
                // (its pending hit), pay its cost or get a 0x1ab (live 2026-10-25).
                bool leapSkill = attack && !IsTotemSkill(player, use.SkillId) && IsLeapSlam(player, gem.Effect);
                if (attack && !leapSkill && !AttackReady(player, use.SkillId, gem.Effect, gem.Level, out double attackSeconds, use.Slot))
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{use.SkillId:x4} {gem.Name}: ignored, the previous attack is still running (attack time {attackSeconds:0.00} s) (t={AreaTime(player):0.000})");
                    continue;
                }
                if (cast && gem.Name is not null && IsDashEffect(SkillsFor(player).EffectId(gem.Effect)))
                {
                    if (!TryCommitDashUse(player, use.SkillId, gem.Effect, (use.X, use.Y), out var dashUse, at, flags: use.Flags))
                    {
                        Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{use.SkillId:x4} {gem.Name}: Dash refused before action admission " +
                            $"at ({use.X}, {use.Y}) (t={AreaTime(player):0.000})");
                        continue;
                    }
                    if (!await CastGemSkill(player, use.SkillId, dashUse!.From, (use.X, use.Y),
                            sequence: use.Slot, dashReceipt: dashUse)) continue;
                    bool dashAcknowledged = false;
                    await Send(player,
                        [.. InstanceProtocol.ActionStarted(player.Id, dashUse.From.X, dashUse.From.Y, use.X, use.Y,
                            use.SkillId, use.Slot, use.Flags), .. InstanceProtocol.ActionEnded(player.Id)],
                        $"0x01ab player skill 0x{use.SkillId:x4} {gem.Name}: accepted Dash from ({dashUse.From.X}, {dashUse.From.Y}) " +
                        $"to ({dashUse.To.X}, {dashUse.To.Y}) (t={dashUse.AcceptedAt:0.000})",
                        still: () => dashAcknowledged = dashUse.ConfirmNativeUse(player));
                    if (!dashAcknowledged) continue;
                    await SendNativeCooldownRefresh(player);
                    ResyncAfter(player, MovementSeconds(player, gem.Effect, gem.Level), gem.Name);
                    continue;
                }
                Mark("ready");
                int action = 0;
                (int px, int py) = (0, 0);
                CooldownUseTicket? cooldownUse = null;
                int chargeBonus = cast && gem.Name is not null ? ShieldChargeBonusPct(player, use.SkillId, gem.Effect) : 0;
                // A lockstep client's forced stationary default attack swings where the player stands. Ordinary ground
                // melee uses take StartLockstepGroundMelee's frame-stepped walk-up before reaching this handler.
                bool inPlace = cast || player.Connection.LockstepClient && use.SkillId == SkillCatalog.FirstSkillId;
                // Flicker Strike on the ground goes to the monster in range nearest to the point (FlickerStrike), and is
                // refused when none is; the server teleports the player, which the client does not.
                bool flicker = cast && gem.Name is not null && SkillsFor(player).ActionHash(gem.Effect) == FlickerStrike.Action;
                (uint Id, (int X, int Y) At, (int X, int Y) Landing)? flickerTo = null;
                LeapSlam.Use? leap = null;
                bool movedByCast = false;
                (int X, int Y) moveLanding = default;
                void StartAction()
                {
                    bool lockstep = player.Connection.LockstepClient;
                    double actionAt = lockstep && player.Connection.LockstepFrame is { } frame ? frame.Start
                        : Math.Max(at, player.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
                    at = Math.Max(at, player.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
                    if (lockstep) StepWalkers(player, actionAt);
                    AdvanceShieldChargeForUse(player, actionAt, attack);
                    if (chargeBonus == 0) EndShieldChargeIfInterrupted(player);
                    CancelMeleeRepeats(player, at);
                    action = Interlocked.Increment(ref player.ActionCount);
                    (px, py) = inPlace ? player.Walk.Position(actionAt) : player.Walk.Order(use.X, use.Y, actionAt);
                    if (inPlace && chargeBonus == 0)
                    {
                        if (lockstep) player.Walk.Cancel(actionAt);
                        else player.Walk.Order(px, py, at);
                    }
                    // Readiness and reservation precede movement: a raw early Dash cannot teleport anyway. Shield Charge walks
                    // there at its own speed instead (ShieldCharge.cs).
                    if (leap is not null) { CommitLeapSlam(player, leap); movedByCast = leap.Landing != leap.From; }
                    else if (flicker && flickerTo is { } to) { player.Walk.Teleport(to.Landing.X, to.Landing.Y, at); movedByCast = to.Landing != (px, py); }
                    else if (chargeBonus == 0 && gem.Name is not null && !IsDashEffect(SkillsFor(player).EffectId(gem.Effect)) && LandsAtAim(player, gem.Effect))
                    {
                        var landing = MovementLanding(player, gem.Effect, (px, py), (use.X, use.Y), use.SkillId, use.Flags);
                        player.Walk.Teleport(landing.X, landing.Y, at);
                        moveLanding = landing;
                        movedByCast = landing != (px, py);
                    }
                    player.ChannelSkill = cast ? use.SkillId : (ushort)0;
                    player.ChannelSkillSequence = use.Slot;
                }
                // With no monster in range, Flicker Strike is not used: no cost, cooldown use or charge.
                bool noFlickerTarget = false;
                bool FlickerAdmit()
                {
                    double t = Math.Max(at, player.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
                    flickerTo = FlickerDestination(player, gem.Effect, use.SkillId, player.Walk.Position(t), (use.X, use.Y), 0, t);
                    noFlickerTarget = flickerTo is null;
                    return !noFlickerTarget;
                }
                if (leapSkill)
                {
                    if (!TryBeginAttackUse(player, use.SkillId, gem.Effect, at,
                        acceptedAt => { at = acceptedAt; StartAction(); }, out cooldownUse,
                        acceptedAt => (leap = PlanLeapSlam(player, use.SkillId, gem.Effect, (use.X, use.Y), acceptedAt)) is not null))
                        continue;
                }
                else if (cast && gem.Name is not null)
                {
                    if (!TryBeginSkillCooldownUse(player, use.SkillId, gem.Effect,
                        CooldownForSkill(player, use.SkillId),
                        false, StartAction, out cooldownUse, requestedAt: at, admit: flicker ? FlickerAdmit : null))
                    {
                        if (noFlickerTarget) await FlickerRefused(player, gem.Name);
                        continue;
                    }
                }
                else
                {
                    using (ProjectileMutation(player))
                    {
                        if (PlayerActionBlocked(player, at, AreaTime(player))) continue;
                        StartAction();
                        // A lockstep client's default attack at the ground is a swing where it stands (inPlace), which takes
                        // the attack time: the next use waits for it, as after a swing at a monster (Attack).
                        if (inPlace && !cast && use.SkillId == SkillCatalog.FirstSkillId)
                        {
                            NotePlayerAttack(player);
                            player.NextAttackAt = AttackTiming.After(LockstepScheduledAt(player, at), player.NextAttackAt,
                                Math.Max(TickSeconds, DefaultAttackSeconds(player, use.Slot, CharacterPlayerStats(player)) / PlayerActionFactor(player)));
                        }
                    }
                }
                Mark("begin");
                bool projectileRuntime = cast && gem.Name is not null && (UsesProjectileRuntime(gem.Effect) || leapSkill || chargeBonus != 0);
                double? swingAt = attack || player.Connection.LockstepClient ? LockstepScheduledAt(player, at) : null;
                Action? startCharge = null;
                Task<bool>? castTask = projectileRuntime ? CastGemSkillWithAdmission(player,
                    chargeBonus != 0 ? start => startCharge = start : null, use.SkillId,
                    (px, py), (use.X, use.Y), sequence: use.Slot, actionToken: cooldownUse?.ActionToken ?? action, cooldownUse: cooldownUse,
                    swingAt: swingAt, leap: leap, movedByCast: movedByCast) : null;
                if (chargeBonus != 0 && !await castTask!) continue;
                if (castTask is { IsCompletedSuccessfully: true, Result: false }) continue;
                Mark("cast");
                bool acknowledged = false;
                bool predictedLeap = cast && PredictsLeapSlam(player, SkillsFor(player).EffectId(gem.Effect), use.Slot);
                Func<Task> echo = async () =>
                {
                    await Send(player, [],
                    $"{(predictedLeap ? "0x01ac predicted Leap Slam acknowledged" : "0x01ab action started")} {use} at ({px}, {py}){(cast ? " (gem skill cast)" : $", walk ends at {player.Walk.Destination} in {player.Walk.ArrivesAt - at:0.00} s")} (t={at:0.000})",
                    still: () => acknowledged = cast
                        ? SkillAcknowledgementAllowed(player, gem.Effect, use.SkillId,
                            () => cooldownUse?.CommitNativeUse(player, use.SkillId) ?? PlayerActionCurrent(player, action))
                        : (cooldownUse?.CommitNativeUse(player, use.SkillId) ?? PlayerActionCurrent(player, action)),
                    atGate: () =>
                    {
                        Interlocked.Exchange(ref startCharge, null)?.Invoke();
                        byte[] reply = [.. InstanceProtocol.ActionStarted(player.Id, px, py, use.X, use.Y, use.SkillId, use.Slot, use.Flags),
                            .. cast ? InstanceProtocol.ActionEnded(player.Id) : []];
                        return predictedLeap ? LeapSlamAcknowledgement(player, reply) : reply;
                    });
                    if (acknowledged) await SendNativeCooldownRefresh(player);
                    else if (ServerTrace.Latency)
                        Console.WriteLine($"[gateway] {player.Connection.Remote} action echo refused 0x{use.SkillId:x4}:"
                            + $" ActionCount={player.ActionCount} action={action} life={player.PlayerLife} closed={player.Closed}");
                };
                // The action acknowledgement is sent here and not queued behind the projectile presentation: it is a
                // state reply that the client needs within this use, and the queue's own records (creates, moves, bursts)
                // took long enough that the next click had already started a new action, so the echo was refused as stale
                // and the client never ran the action (live 2026-11-17: 51 of 59 Kinetic Blast casts, no casting animation).
                await echo();
                Mark("echo");
                if (chargeBonus != 0 && !acknowledged) continue;
                if (!projectileRuntime && !acknowledged) continue;
                if (!cast && HasOpenInteraction(player) && SkillsFor(player).ActionName(use.SkillId) == "Move")
                    await CloseInteractionsIfMovingAway(player, (use.X, use.Y), at);
                if (cooldownUse?.Current(player) == false) continue;
                if (flickerTo is { } flickerDestination)
                {
                    await FlickerArt(player, gem.Gem, (px, py), flickerDestination.Landing);
                    await SendWorldRecord(player, player.Id, InstanceProtocol.Teleport(player.Id, flickerDestination.Landing.X, flickerDestination.Landing.Y),
                        $"0x01b7 {gem.Name} to ({flickerDestination.Landing.X}, {flickerDestination.Landing.Y}) for monster 0x{flickerDestination.Id:x} "
                        + $"at ({flickerDestination.At.X}, {flickerDestination.At.Y}) (t={at:0.000})");
                }
                if (flicker) await GrantFlickerSpeed(player, use.SkillId);
                if (cast && gem.Name is not null && movedByCast && IsFlameDashEffect(SkillsFor(player).EffectId(gem.Effect)))
                    await SendWorldRecord(player, player.Id,
                        FlameDashGroundSegment(player, use.SkillId, gem.Effect, use.Slot, (px, py), moveLanding),
                        $"0x02c8 Flame Dash burning ground ({px}, {py}) -> ({moveLanding.X}, {moveLanding.Y})");
                // Flicker Strike strikes from where it lands, at the monster it went to.
                var (strikeFrom, strikeAt) = flickerTo is { } struck ? (struck.Landing, struck.At) : ((px, py), (use.X, use.Y));
                if (cast && !projectileRuntime) _ = CastGemSkill(player, use.SkillId, strikeFrom, strikeAt, targetId: flickerTo?.Id ?? 0, sequence: use.Slot,
                    actionToken: cooldownUse?.ActionToken ?? action, cooldownUse: cooldownUse, swingAt: swingAt, movedByCast: movedByCast);
                // Reconcile the skills that use the immediate landing estimate once their action has finished.
                if (leap is { } jump)
                    ResyncAfter(player,
                        Math.Max(0, jump.At + jump.Timing.End - AreaTime(player)), gem.Name!);
                else if (cast && gem.Name is not null && chargeBonus == 0 && LandsAtAim(player, gem.Effect))
                    ResyncAfter(player, MovementSeconds(player, gem.Effect, gem.Level, use.SkillId), gem.Name);
                if (!PlayerActionCurrent(player, action)) continue;
                if (MoveMode == "teleport")
                    await SendWorldRecord(player, player.Id, InstanceProtocol.Teleport(player.Id, use.X, use.Y),
                        $"0x01b7 teleport to ({use.X}, {use.Y})", still: () => PlayerActionCurrent(player, action));
                else if (MoveMode == "move")
                {
                    await SendWorldRecord(player, player.Id, InstanceProtocol.MoveTo(player.Id, use.X, use.Y, MoveSpeed),
                        $"0x01ba move to ({use.X}, {use.Y}) speed {MoveSpeed}", still: () => PlayerActionCurrent(player, action));
                }
                }
                finally
                {
                    Mark("tail");
                    double useTotal = Stopwatch.GetElapsedTime(useStarted).TotalMilliseconds;
                    if (phases is not null && useTotal > 40)
                        Console.WriteLine($"[slow-use] {player.Connection.Remote} total={useTotal:0} cpu={(ThreadCpuMilliseconds() - useCpuStarted):0}"
                            + $" {string.Join(" ", phases)}");
                }
            }
            else if (InstanceProtocol.IsLockstepRequest(id))
            {
                // The client chose lockstep (07 §6): it is served as it asks.
                OnLockstepRequest(player, id, bytes);
                if (id == InstanceProtocol.LockstepMoveUpdateId && !HeldLockstepAimedChannel(player))
                    await AimLockstepChannel(player);
            }
            else if (id == InstancePause.RequestId)
                await SetPaused(player, InstancePause.ReadRequest(bytes));
            else if (id == InstanceWaypoints.LabyrinthChosenId && bytes.Length >= InstanceWaypoints.LabyrinthChosenLength)
                await ChooseLabyrinth(player, bytes[2]);
            else if (id == InstanceWaypoints.AscendancyChosenId && bytes.Length >= InstanceWaypoints.AscendancyChosenLength)
                await ChooseAscendancyClass(player, bytes[2]);
            else if (id == InstanceWaypoints.DescendancyChosenId && bytes.Length >= InstanceWaypoints.DescendancyChosenLength)
                await ChooseDescendancy(player, bytes[2]);
            else if (id == InstanceProtocol.UseInstantSkillAtLocationId)
            {
                var (x, y, skill) = InstanceProtocol.ReadUseInstantSkillAtLocation(bytes);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0190 instant skill 0x{skill:x4} at ({x}, {y})");
                if (SkillsFor(player).IsGemSkill(skill)) _ = UseInstantSkillAtLocation(player, skill, (x, y));
            }
            else if (id == InstanceProtocol.UseInstantSkillId)
            {
                // An instant skill (skill_is_instant: Haste, Righteous Fire) has no action: the walk and a pending hit go on,
                // and the client waits for no echo.
                var (target, skill) = InstanceProtocol.ReadUseInstantSkill(bytes);
                var spot = player.Walk.Position(AreaTime(player));
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0191 instant skill 0x{skill:x4} on 0x{target:x} at ({spot.X}, {spot.Y})");
                if (SkillsFor(player).IsGemSkill(skill)) _ = UseInstantSkill(player, skill, target, spot);
            }
            else if (id == InstanceTransfer.UseSkillOnObjectId)
            {
                var use = InstanceTransfer.ReadUseSkillOnObject(bytes);
                // In the background: the player may first walk to the target, and the read loop must keep answering ticks.
                _ = OnUseObject(player, use, player.ClockAt(readAt));
            }
            else if (id == InstanceProtocol.BindSkillId)
            {
                var (slot, skill) = InstanceProtocol.ReadBindSkill(bytes);
                string? entry = SkillBarEntry(player, skill);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x004e bind skill 0x{skill:x4} ({(entry is null ? "unknown, not saved" : entry == "" ? "empty" : entry)}) to slot {slot}: {Convert.ToHexString(bytes)}");
                if (entry is not null && slot < InstanceProtocol.SkillBarSlots)
                {
                    SkillBarLayout.Bind(CharacterName(player), slot, entry);
                    // Echo 0x0053 so the client updates its server-authoritative skill bar cache (instance +0xc7d8);
                    // otherwise the next 0x02bc Actor update (e.g. unsocketing a support gem) rebuilds the bar from stale data.
                    await SendSkillBar(player);
                }
            }
            else if (id is InstanceProtocol.AttackInPlaceId or InstanceProtocol.SlotMask52Id)
            {
                // A slot option the client already applied (its bit of instance +0xcfd0 / +0xcfd2); saved so the next
                // 0x53 sends it back instead of clearing it. The first box this character touches starts from the
                // slots' own defaults, so ticking one slot does not untick the attack-in-place skill in another one;
                // from then on the saved mask is the whole truth, as the player left it.
                var (slot, on) = InstanceProtocol.ReadSlotOption(bytes);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x{id:x4} {(id == InstanceProtocol.AttackInPlaceId ? "always attack without moving" : "slot option 0x52")} slot {slot} {(on ? "on" : "off")}");
                if (slot < 16)
                {
                    bool attackInPlace = id == InstanceProtocol.AttackInPlaceId;
                    string character = CharacterName(player);
                    ushort? seed = attackInPlace
                        ? AttackInPlaceDefaults(player, SkillBarIds(player, CharacterSaves.Load(character)?.SkillBar))
                        : null;
                    CharacterSaves.SetSkillBarOption(character, attackInPlace, slot, on, seed);
                }
            }
            else if (id == InstanceEntities.AllocatePassiveId)
            {
                var (u2, node, u6) = InstanceEntities.ReadAllocatePassive(bytes);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0034 allocate passive {node} ({PassivesFor(player).Find(node)?.Name ?? "unknown"}), " +
                    $"node high16 {u2}, expansion {u6}");
                if (u2 == 0 && u6 != 0)
                    await ChangeClusterPassive(player, node, allocate: true);
                else if (u2 == 0)
                {
                    string? error = await ChangePassives(player, [node], allocate: true, "Passive Skill Tree");
                    if (error is not null) await player.Commands!.Reply(error);
                }
                else
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} allocation refused: 32-bit passive ids are not supported");
                    await SendPassives(player);
                }
            }
            else if (id == InstanceEntities.MasteryEffectId)
            {
                var (node, effect) = InstanceEntities.ReadMasteryEffect(bytes);
                await ChangePassives(player, [node], allocate: true, "Passive Skill Tree mastery", effect);
            }
            else if (id == InstanceEntities.RefundPassiveId)
            {
                var (node, expansion, gold) = InstanceEntities.ReadRefundPassive(bytes);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0036 refund passive {node} ({(node <= ushort.MaxValue ? PassivesFor(player).Find((ushort)node)?.Name : null) ?? "unknown"})" +
                    $"{(expansion ? ", expansion node" : "")}, {(gold ? "gold respec" : "refund points")}: {Convert.ToHexString(bytes)}");
                if (expansion && node <= ushort.MaxValue)
                    await ChangeClusterPassive(player, (ushort)node, allocate: false);
                else if (!expansion && node <= ushort.MaxValue)
                {
                    if (gold)
                    {
                        string? error = await ChangePassives(player, [(ushort)node], allocate: false, "Respecialisation", goldRespec: true);
                        if (error is not null) await player.Commands!.Reply(error);
                    }
                    else
                    {
                        string? error = await ChangePassives(player, [(ushort)node], allocate: false, "Refund points", refundPoints: true);
                        if (error is not null) await player.Commands!.Reply(error);
                    }
                }
                else
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} refund refused: 32-bit passive ids are not supported");
                    await SendPassives(player);
                }
            }
            else if (id == InstanceEntities.RefundAllPassivesId)
            {
                // "Refund All" (after the client's Yes): every passive but the ascendancy ones, for their full gold cost.
                var nodes = PassiveState(player).Nodes.Where(n => PassivesFor(player).Find(n) is not { IsAscendancy: true }).ToList();
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0037 refund all passives ({nodes.Count})");
                await ChangePassives(player, nodes, allocate: false, "Respecialisation, Refund All", goldRespec: true);
            }
            else if (id == InstanceEntities.PanelToggleId)
            {
                bool opened = bytes.Length >= 3 && bytes[2] != 0;
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0123 panel {(opened ? "opened" : "closed")}");
                if (opened) await SendAchievementProgress(player);
            }
            else if (id == InstanceEntities.CancelBuffId)
            {
                uint raw = InstanceEntities.ReadCancelBuff(bytes);
                if (raw <= ushort.MaxValue) await GameEvents.RunBuffCancel(player, (ushort)raw);
            }
            else if (id == InstanceProtocol.TutorialCompleteId)
                await CompleteTutorial(player, InstanceProtocol.ReadTutorialComplete(bytes));
            else if (id == InstanceItems.DropItemId)
                await DropItem(player);
            else if (id == InstanceItems.CommandId)
            {
                var command = InstanceItems.ReadCommand(bytes);
                if (command.Id == InstanceItems.DestroyCursorItemCommand) await DestroyHeldItem(player);
                else if (command.Id == InstanceItems.WarpToHideoutCommand) await WarpToHideout(player, "/hideout");
                else if (command.Id == InstanceItems.SaveHideoutCommand) await SaveHideoutLayout(player);
                else if (command.Id is InstanceItems.ForceResyncCommand or InstanceItems.ForceResyncPlayerCommand) await SendResync(player, "the client asked");
                else if (command.Id == InstanceItems.AfkCommand && player.Connection.LockstepClient) AnswerLockstepProbe(player, readAt);
                else Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0016 command {command} (not handled)");
            }
            else if (id == InstanceWaypoints.VisitHideoutId)
            {
                var visit = InstanceWaypoints.ReadVisitHideout(bytes);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x017a visit the hideout of {visit.Character} (from object 0x{visit.Waypoint:x}, u8 {visit.Flag})");
                await WarpToPlayerHideout(player, visit.Character, "Visit Hideout");
            }
            else if (id == InstanceWaypoints.VisitMenagerieId)
            {
                // The World panel's Menagerie pin: the same layout as 0x17a (whose Menagerie, waypoint object, u8).
                var visit = InstanceWaypoints.ReadVisitHideout(bytes);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x017b visit the Menagerie of {visit.Character} (from object 0x{visit.Waypoint:x}, u8 {visit.Flag})");
                if (AreaTransitionCatalog.HashOf(WaypointCatalog.MenagerieHub) is { } menagerie)
                    await ChangeArea(player, new PendingTransfer(menagerie, 0, false, AtSpawn: true), "World panel Menagerie pin");
            }
            else if (id == InstanceWaypoints.VisitChayulaHubId)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0182 World panel Monastery of the Keepers pin");
                if (AreaTransitionCatalog.HashOf(WaypointCatalog.ChayulaHub) is { } monastery)
                    await ChangeArea(player, new PendingTransfer(monastery, 0, false, AtSpawn: true), "World panel Monastery of the Keepers pin");
            }
            else if (id == InstanceWaypoints.VisitHeistHubId)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0184 World panel Rogue Harbour pin");
                if (AreaTransitionCatalog.HashOf(WaypointCatalog.HeistHub) is { } harbour)
                    await ChangeArea(player, new PendingTransfer(harbour, 0, false, AtSpawn: true), "World panel Rogue Harbour pin");
            }
            else if (id == InstanceWaypoints.VisitKingsmarchId)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0181 World panel Kingsmarch pin");
                if (AreaTransitionCatalog.HashOf(WaypointCatalog.Kingsmarch) is { } kingsmarch)
                    await ChangeArea(player, new PendingTransfer(kingsmarch, 0, false, AtSpawn: true), "World panel Kingsmarch pin");
            }
            else if (id == InstanceWaypoints.VisitSanctumId)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x017e World panel Forbidden Sanctum pin");
                if (AreaTransitionCatalog.HashOf(WaypointCatalog.SanctumHub) is { } sanctum)
                    await ChangeArea(player, new PendingTransfer(sanctum, 0, false, AtSpawn: true), "World panel Forbidden Sanctum pin");
            }
            else if (id == InstanceWaypoints.VisitDelveHubId)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x017d World panel Mine Encampment pin");
                if (AreaTransitionCatalog.HashOf(WaypointCatalog.DelveHub) is { } delve)
                    await ChangeArea(player, new PendingTransfer(delve, 0, false, AtSpawn: true), "World panel Mine Encampment pin");
            }
            else if (id == InstanceItems.UnsocketGemId)
                await UnsocketGem(player, InstanceItems.ReadUnsocketGem(bytes));
            else if (id == InstanceItems.SocketGemId)
                await SocketGem(player, InstanceItems.ReadSocketGem(bytes));
            else if (id == InstanceItems.LevelUpGemId)
                await LevelUpGem(player, InstanceItems.ReadLevelUpGem(bytes));
            else if (id == InstanceItems.LevelUpAllGemsId)
                await LevelUpAllGems(player);
            else if (id == InstanceItems.LockGemId)
                await LockGem(player, InstanceItems.ReadLockGem(bytes));
            else if (id == InstanceItems.PlaceItemId)
                await PlaceItem(player, InstanceItems.ReadPlaceItem(bytes));
            else if (id == InstanceItems.ItemActionId)
                await HeldOntoItem(player, InstanceItems.ReadItemAction(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceItems.SplitStackId)
                await SplitStack(player, InstanceItems.ReadSplitStack(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceItems.UseItemClickId)
                await UseItem(player, InstanceItems.ReadUseItemClick(bytes), Convert.ToHexString(bytes), sendResult: true);
            else if (id == InstanceItems.UseItemId)
                await UseItem(player, InstanceItems.ReadUseItem(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceItems.UseFlaskId)
                await UseFlask(player, InstanceItems.ReadUseFlask(bytes));
            else if (id == InstanceItems.UseItemOnId)
                await UseItemOn(player, InstanceItems.ReadUseItemOn(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceItems.UseItemTypeOnId)
            {
                // Shift held: the currency stays on the cursor and is named by type; use a stack of it from the inventories,
                // the main inventory first.
                var again = InstanceItems.ReadUseItemTypeOn(bytes);
                var items = ItemsOf(player);
                byte[] rows = [PlayerInventories.MainInventory, .. Enumerable.Range(0, InstanceInventories.PlayerDefaults.Length).Select(r => (byte)r)];
                var stack = rows.Select(r => (Row: r, Item: items.Items(r).FirstOrDefault(i => i.Item.TypeHash == again.TypeHash))).FirstOrDefault(s => s.Item is not null);
                // None in the inventories: the currency was taken from the open stash, so use a stack of it from a stash tab.
                var stash = StashOf(player);
                (int Tab, InventoryItem Item)? inStash = null;
                for (int t = 0; stack.Item is null && inStash is null && t < stash.Count; t++)
                    if (!stash.IsPublic(t) && stash.Inventory(t).Items.FirstOrDefault(i => i.Item.TypeHash == again.TypeHash) is { } found) inStash = (t, found);
                if (inStash is var (stashTab, stashItem))
                    await UseItemOn(player, new InstanceItems.UseItemOn(stash.InventoryOf(stashTab), stashItem.Id,
                        again.TargetInventoryId, again.TargetId), Convert.ToHexString(bytes));
                else if (stack.Item is null) Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0072 use type 0x{again.TypeHash:x8} again: none left");
                else await UseItemOn(player, new InstanceItems.UseItemOn(PlayerInventories.InventoryId(stack.Row), stack.Item.Id,
                    again.TargetInventoryId, again.TargetId), Convert.ToHexString(bytes));
            }
            else if (id == InstanceItems.InventoryRequestId)
                await QuickMoveHeistNpcEquipment(player, InstanceItems.ReadInventoryRequest(bytes));
            else if (id == InstanceItems.InventoryQuickMoveId)
                await QuickMoveHeistNpcEquipment(player, InstanceItems.ReadInventoryQuickMove(bytes));
            else if (id == InstanceSkinTransfers.BindId)
                await NativeSkinTransfer(player, InstanceSkinTransfers.ReadBind(bytes));
            else if (id == InstanceSkinTransfers.ReclaimId)
                await NativeReclaimSkin(player, InstanceSkinTransfers.ReadReclaim(bytes));
            else if (id == InstanceItems.PickUpItemId)
                await PickUpItem(player, InstanceItems.ReadPickUpItem(bytes));
            else if (id == InstanceProtocol.ExitToCharacterSelectId)
            {
                // Esc → "Exit to Character Selection" or the hardcore death screen's Exit.
                // Send S→C 0x02b4 (WorldAreas row 1 CharacterSelect, 0x4884) so the client displays
                // "Entering Character Select" and replies with C→S 0x02cd; then flush & send clean 0x0026.
                if (player.PendingCharacterSelectExit) continue;
                player.PendingCharacterSelectExit = true;
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0153 exit to character selection: sending 0x02b4 CharacterSelect (0x{InstanceProtocol.CharacterSelectAreaHash:x4})");
                await Send(player, InstanceTransfer.ChangeArea(InstanceProtocol.CharacterSelectAreaHash, 0),
                    $"0x02b4 entering character select (0x{InstanceProtocol.CharacterSelectAreaHash:x4})");
                _ = Task.Delay(2000).ContinueWith(_ =>
                {
                    if (player.PendingCharacterSelectExit && !player.Closed)
                        _ = player.Frames.Run(() => FinishCharacterSelectExit(player));
                });
            }
            else if (id == InstanceProtocol.ExitToLoginId)
            {
                // Fallback when 0x001a reaches the handler before OnClientData's immediate logout path.
                if (Interlocked.Exchange(ref player.ExitToLoginHandled, 1) == 0)
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x001a exit to log in screen: flushing progress, closing login and instance connections");
                    if (player.Ticket != 0)
                        await InstanceControl.CallAsync(new ControlRequest { Op = "logout", Ticket = player.Ticket });
                    FlushAreaProgress(player);
                    player.Connection.CloseReason = "exit to log in screen (0x001a)";
                    player.Closed = true;
                    player.InvalidateIncomingHits();
                }
                return;
            }
            else if (id == InstanceProtocol.TutorialCompleteId)
            {
                byte code = bytes.Length >= 3 ? bytes[2] : (byte)0;
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x020a tutorial complete (0x{code:x2})");
                if (code == InstanceProtocol.TutorialSkipAll)
                    await SetQuestFlagsHeld(player, [Quests.CompletedAllTutorials]);
            }
            else if (id == InstanceProtocol.ResurrectId)
                _ = Resurrect(player, InstanceProtocol.ReadResurrectInTown(bytes));
            else if (id == InstanceNpcs.DialogueClosedId)
                await CloseNpcDialogue(player);
            else if (id == InstanceStash.RequestTabId)
            {
                var (tab, kind) = InstanceStash.ReadRequestTab(bytes);
                await SendStashTab(player, tab, kind);
            }
            else if (id == InstanceStash.ReorderTabsId)
            {
                // The tab buttons' order, kept with the stash (personal, guild or trade by kind) and sent back in the tab lists.
                var (kind, order) = InstanceStash.ReadReorderTabs(bytes);
                if (!CanManageStashKind(player, kind) || StashByKind(player, kind) is not { } target) continue;
                int moved = target.SetPositions(order);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00cc stash {kind} tab order: {string.Join(", ", order.Select(o => $"{o.Index}->{o.Position}"))} ({moved} moved, saved)");
                if (kind == InstanceStash.Guild && moved > 0)
                {
                    foreach (var peer in GameEvents.ResolveGuildPeers(player))
                    {
                        if (peer is not { HasClient: true, Closed: false, LeavingArea: false, Connection.Outbox: { } outbox }) continue;
                        try
                        {
                            await outbox(InstanceStash.EncodeReorderTabs(InstanceStash.Guild, order),
                                $"0x00cc broadcast guild stash tab order ({order.Count} tab(s))");
                        }
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                    }
                }
            }
            else if (InstanceProtocol.IsChannelUpdate(id))
            {
                // A channelled skill (Cyclone, Winter Orb) goes on, aimed at this cell: Cyclone walks toward it
                // (ChannelAimedAt). Its hits run on their own timer (RunChannel), at the skill's attack rate, wherever the
                // player is. Without this update the channel counts as silent and ends (ChannelSilenceSeconds).
                var (cx, cy) = InstanceProtocol.ReadChannelUpdate(bytes);
                double t = player.ClockAt(readAt);
                ushort? changedAim = null;
                using (ProjectileMutation(player))
                {
                    t = Math.Max(t, player.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
                    if (PlayerActionBlocked(player, t, AreaTime(player))) continue;
                    if (ApplyChannelAim(player, cx, cy, t) && !player.Connection.LockstepClient)
                        changedAim = player.ChannelSkillSequence;
                    player.LastChannelUpdateAt = AreaTime(player);
                }
                if (changedAim is ushort sequence)
                    await SendChannelAim(player, sequence, (cx, cy));
            }
            else if (id == InstanceProtocol.ChannelEndId)
            {
                bool hadChannel;
                int channelGen;
                ushort endedSequence;
                (int X, int Y)? landing;
                lock (LevelUpdatesFor(player).Gate)
                {
                    hadChannel = player.ChannelSkill != 0;
                    channelGen = player.ChannelGeneration;
                    endedSequence = player.ChannelSkillSequence;
                    landing = EndChannel(player, player.ClockAt(readAt));
                }
                if (hadChannel && !player.Connection.LockstepClient)
                    await EmitPredictiveChannelFinished(player, channelGen, endedSequence, landing);
            }
            else if (id == InstanceProtocol.RetargetActionId)
                await RetargetAction(player, InstanceProtocol.ReadRetargetAction(bytes), readAt);
            else if (id == InstanceProtocol.SwapWeaponsId)
            {
                // The weapon swap: the hands' rows and the other set's trade their items (the client shows the active set in
                // the hands' rows), each of the four inventories gets its new contents (0xbf), S→C 0xde gives the set (which
                // also ends the client's wait), then what the hands hold, with the new set's stats and skills (13-items.md).
                var swapItems = ItemsOf(player);
                byte wanted = (byte)(bytes[2] == 1 ? 1 : 0);
                byte finalWeaponSet;
                List<(byte[] Packet, string Label)> swapOutputs = [];
                // The current-set decision, mutation, qualification and final-set capture share one boundary. Outputs
                // and SendEquipment may suspend after it; the 0xde set below is the one committed by this message.
                using (ProjectileMutation(player, advance: false, phase: "weapon swap"))
                {
                    finalWeaponSet = swapItems.WeaponSet;
                    if (wanted != swapItems.WeaponSet)
                    {
                        var sourcesBefore = PlayerSkillSourcesLocked(player);
                        var before = new[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1, PlayerInventories.Weapon2, PlayerInventories.Offhand2 }
                            .ToDictionary(r => r, r => swapItems.Items(r).ToArray());
                        swapItems.SwapWeaponSets();
                        swapItems.WeaponSet = wanted;
                        finalWeaponSet = wanted;
                        RemoveRallyingCryOnWeaponSwapLocked(player);
                        using (new PlayerStatScope(player, fresh: true))
                        {
                            RecomputePoolsAndSpeed(player);
                            ReconcileBeastCompanionsLocked(player);
                            ReconcileEquipmentSourceLifetimesLocked(player, sourcesBefore);
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
                foreach (var (packet, label) in swapOutputs) await Send(player, packet, label);
                await Send(player, InstanceProtocol.WeaponSet(finalWeaponSet == 1),
                    $"0x00de weapon set {finalWeaponSet + 1}");
                await SendEquipment(player, swapItems);
            }
            else if (id == InstanceStash.RenameTabId)
            {
                // A premium tab renamed in the tab settings; kept with the stash (personal, guild or trade), account-wide.
                var (kind, tab, name) = InstanceStash.ReadRenameTab(bytes);
                if (!CanManageStashKind(player, kind) || StashByKind(player, kind) is not { } target) continue;
                bool done = target.Rename(tab, name);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00cd stash {kind} tab {tab} renamed \"{name}\": {(done ? "saved" : "refused")}");
                await SendTabListAgain(player, kind);
            }
            else if (id == InstanceStash.ColourTabId)
            {
                var (kind, tab, colour) = InstanceStash.ReadColourTab(bytes);
                if (!CanManageStashKind(player, kind) || StashByKind(player, kind) is not { } target) continue;
                bool done = target.SetColour(tab, colour);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00ce stash {kind} tab {tab} colour 0x{colour:x8}: {(done ? "saved" : "refused")} ({Convert.ToHexString(bytes)})");
                await SendTabListAgain(player, kind);
            }
            else if (id == InstanceStash.PublicTabId)
            {
                var (kind, tab, isPublic) = InstanceStash.ReadPublicTab(bytes);
                if (!CanManageStashKind(player, kind) || StashByKind(player, kind) is not { } target) continue;
                bool done = target.SetPublic(tab, isPublic);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00cf stash {kind} tab {tab} public={(isPublic ? "on" : "off")}: {(done ? "saved" : "refused")}");
                await SendTabListAgain(player, kind);
            }
            else if (id == InstanceItems.SetItemNoteId)
            {
                if (!player.StashOpen && !player.GuildStashOpen && !player.TradeStashOpen) continue;
                var req = InstanceItems.ReadSetItemNote(bytes);
                // C→S 0x01a1 with Flag == true (bit 31 set, sender 0x1402fbbf0): pricing an unpriced held cursor item
                // before C→S 0x002a places it into an async-trade merchant tab (non-SSF, trade stash open only).
                if (req.Flag && player.TradeStashOpen && !player.Character.Ssf)
                {
                    var inv = ItemsOf(player);
                    if (inv.SetItemNote(PlayerInventories.Cursor, req.ItemId, req.Note) is { } updatedCursor)
                    {
                        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x01a1 cursor item {req.ItemId} note \"{req.Note}\": saved");
                        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                            inv.NextRevision(PlayerInventories.Cursor), [updatedCursor.Id], [updatedCursor]),
                            $"0x00bf cursor: item {updatedCursor.Id} note updated");
                        continue;
                    }
                }
                AccountStash? targetStash = null;
                if (player.TradeStashOpen && !player.Character.Ssf && TradeStashOf(player) is var trade && trade.TabOf(req.InventoryId) is int)
                    targetStash = trade;
                else if (player.StashOpen && PersonalStashOf(player) is var personal && personal.TabOf(req.InventoryId) is int)
                    targetStash = personal;
                else if (player.GuildStashOpen && !player.Character.Ssf && GuildStashOf(player) is var guild && guild.TabOf(req.InventoryId) is int)
                    targetStash = guild;
                if (targetStash is not null && targetStash.TabOf(req.InventoryId) is int noteTab)
                {
                    var updated = targetStash.SetItemNote(noteTab, req.ItemId, req.Note);
                    Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x01a1 stash tab {noteTab} item {req.ItemId} note \"{req.Note}\": {(updated is not null ? "saved" : "refused")}");
                    if (updated is not null)
                        await SendTabChange(player, targetStash, noteTab, [updated.Id], [updated],
                            $"0x00bf stash tab {noteTab}: item {updated.Id} note updated");
                }
            }
            else if (id == InstanceAsyncTrade.RequestHistoryId)
                await SendAsyncTradeHistory(player);
            else if (id == InstanceAsyncTrade.TakeEarningsId)
                await TakeAsyncTradeEarnings(player, InstanceAsyncTrade.ReadTakeEarnings(bytes));
            else if (id == InstanceStash.MoveToTabId)
                await MoveToStash(player, InstanceStash.ReadMoveToTab(bytes));
            else if (id == InstanceStash.QuickPlaceInTabId)
                await QuickPlaceInStash(player, InstanceStash.ReadQuickPlaceInTab(bytes));
            else if (id == InstanceStash.SubTabClickId)
                await SubTabClickInStash(player, InstanceStash.ReadSubTabClick(bytes));
            else if (id == InstanceStash.MoveToAffinityTabId)
                await MoveToAffinityTab(player, InstanceStash.ReadMoveToAffinityTab(bytes), Convert.ToHexString(bytes));
            else if (id == InstanceStash.SetAffinitiesId)
                await SetTabAffinities(player, InstanceStash.ReadSetAffinities(bytes));
            else if (id == InstanceStash.TakeFromTabId)
            {
                var move = InstanceStash.ReadTakeFromTab(bytes);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00e3 take item {move.ItemId} from stash tab {move.Tab} " +
                    $"({move.Kind:x2} {move.TokenLo:x2} {move.TokenHi:x2} {move.Bulk:x2}, {(move.Bulk != 0 ? "bulk" : "one stack")})");
                await TakeFromStash(player, move.Tab, move.ItemId, all: move.Bulk != 0, kind: move.Kind);
            }
            else if (id == InstanceStash.TakeBlightOilId)
            {
                if (!player.StashOpen && !player.GuildStashOpen) continue;
                var move = InstanceStash.ReadTakeBlightOil(bytes);
                if (move.Kind == InstanceStash.Guild && (player.Character.Ssf || !player.GuildStashOpen)) continue;
                if (move.Kind == InstanceStash.Personal && !player.StashOpen) continue;
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00f5 take blight oil {move.ItemId} from stash kind {move.Kind} tab {move.Tab} (panelState {move.UiPanelState})");
                if (move.Tab <= int.MaxValue)
                    await TakeBlightOil(player, (int)move.Tab, move.ItemId, move.Kind);
            }
            else if (id == InstanceHarvest.MoveItemId)
            {
                if (!player.StashOpen) continue;
                var move = InstanceHarvest.ReadMoveItem(bytes);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00f9 storage take-out item {move.ItemId} from tab {move.Tab} (flags 0x{move.Flags:x2}, into {move.IntoStation})");
                await TakeFromStash(player, move.Tab, move.ItemId, all: false, kind: InstanceStash.Personal);
            }
            else if (id == InstanceStash.MoveTabId && bytes.Length >= InstanceStash.MoveTabLength)
            {
                // A tab into a folder, out of one, or (its own index as the folder) into a new one; the list goes out again.
                var (kind, tab, folder) = InstanceStash.ReadMoveTab(bytes);
                if (!CanManageStashKind(player, kind) || StashByKind(player, kind) is not { } target) continue;
                int? done = target.MoveToFolder(tab, folder);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00d1 stash {kind} tab {tab} into folder {(folder == ushort.MaxValue ? "none" : folder == tab ? "(new)" : folder)}: " +
                    (done is int f ? f == ushort.MaxValue ? "out of its folder" : $"in folder {f}" : "refused"));
                await SendTabListAgain(player, kind);
                if (kind == InstanceStash.Personal && done is int newFolder && folder == tab && newFolder != ushort.MaxValue)
                    await SendPersonalStashTabId(player, (ushort)newFolder, $"{newFolder + 1:x8}");
            }
            else if (id == InstanceStash.DeleteFolderId && bytes.Length >= InstanceStash.DeleteFolderLength)
            {
                var (kind, folder) = InstanceStash.ReadDeleteFolder(bytes);
                if (!CanManageStashKind(player, kind) || StashByKind(player, kind) is not { } target) continue;
                bool done = target.DeleteFolder(folder);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00d2 stash {kind} folder {folder} deleted: {(done ? "its tabs moved out" : "refused")}");
                await SendTabListAgain(player, kind);
            }
            else if (id == InstanceNpcs.OptionId)
                await ChooseNpcOption(player, InstanceNpcs.ReadOption(bytes));
            else if (id == InstanceHideouts.SelectId)
                await SelectHideout(player, InstanceHideouts.ReadSelect(bytes));
            else if (id == InstanceRulesets.ActionId && await Client.DispatchRulesetAsync(player, bytes))
                continue;
            else if (id == InstanceChat.ChatId)
            {
                var chat = InstanceChat.ReadChat(bytes);
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0015 chat \"{chat.Text}\" ({chat.Links.Count} linked)");
                // Plain lines are the Chat module's.
                if (chat.IsCommand)
                {
                    if (Client.IsCommand(chat.Text) && await Client.DispatchCommandAsync(player, chat.Text))
                        continue;
                    // A preceding slash command can finish a Game + Commands reload in this same batch.
                    // Resolve the current league's builders after that wave, retaining Gateway sinks/mod fallback.
                    // Run this table: an unload while the command runs empties conn.Commands (Program.ForgetCommands).
                    var commands = Program.Commands(player);
                    player.Commands = commands;
                    await commands.RunAsync(chat.Text);
                }
            }
            else if (id == InstanceTransfer.ChooseInstanceId)
            {
                byte row = bytes[2];
                if (player.Chooser is not { } chooser) { Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00af row {row} without a list"); continue; }
                player.Chooser = null;
                bool isNew = row == InstanceTransfer.NewInstanceRow;
                if (!isNew && row >= chooser.Ids.Count) { Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00af row {row} out of range"); continue; }
                ulong instance = isNew ? 0 : chooser.Ids[row];
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00af chose {(isNew ? "a new instance" : $"instance {instance}")} of 0x{chooser.Area:x4}");
                await ChangeArea(player,
                    new PendingTransfer(chooser.Area, instance, isNew, AtWaypoint: chooser.AtWaypoint)
                    { ArrivalEntrance = chooser.ArrivalEntrance }, $"chooser row {row}");
            }
            else if (id == InstanceWaypoints.TravelId)
                _ = TravelByWaypoint(player, InstanceWaypoints.ReadTravel(bytes));
            else if (id == InstanceSocial.FriendAddId)
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0080 add friend \"{InstanceSocial.ReadFriendAdd(bytes).Name}\" (not handled yet)");
            else if (id == InstanceProtocol.AreaGeneratedId)
            {
                var (clientTileHash, clientDoodadHash) = InstanceProtocol.ReadAreaGenerated(bytes);
                player.ClientTileHash = clientTileHash;
                player.ClientDoodadHash = clientDoodadHash;
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x007d area generated: tileHash=0x{clientTileHash:x8} ({clientTileHash}) doodadHash=0x{clientDoodadHash:x8} ({clientDoodadHash})");
                uint serverTileHash = player.Layout.Level?.TileHash ?? 0;
                bool tileHashMismatch = serverTileHash != 0 && clientTileHash != 0 && clientTileHash != serverTileHash;
                if (tileHashMismatch)
                {
                    player.LevelDesynced = true;
                    Console.WriteLine($"[gateway] {player.Connection.Remote} level desync in {player.Layout.Id} seed {player.Seed}: client tileHash={clientTileHash} (0x{clientTileHash:x8}) != server tileHash={serverTileHash} (0x{serverTileHash:x8}) (doodadHash={clientDoodadHash})");
                }
                if (!player.Connection.PlayerSent)
                {
                    // The first player whose client is ready generates the area (its monsters and objects, once); every
                    // player then enters it.
                    bool generates = !player.Area.Generated;
                    player.Area.Generated = true;
                    // First visit with scripts/walkmaps running: the map is written while the client generates the area.
                    if (player.Layout.Walkable is null && await AreaLayouts.WaitFor(player.Layout, player.Seed) is { } fresh)
                    {
                        player.Use(fresh);
                        Console.WriteLine($"[gateway] {player.Connection.Remote} walk map captured: {fresh.Describe()}");
                    }
                    if (player.Layout.AreaTransitions.Length == 0 && await AreaLayouts.WaitForTransitions(player.Layout, player.Seed) is { } withTransitions)
                    {
                        player.Use(withTransitions);
                        Console.WriteLine($"[gateway] {player.Connection.Remote} area transitions captured: {withTransitions.Describe()}");
                    }
                    // Waypoint travel: next to the destination's waypoint instead of the area's spawn. A generated area's
                    // waypoint is only known from the walk-map recorder's capture for this seed (live.ps1 -Walkmaps).
                    // Back through a town portal: where the portal stands (OpenTownPortal).
                    var (arrivalLayout, arrivalNote) = ArrivalLayout(player);
                    if (!ReferenceEquals(arrivalLayout, player.Layout)) player.Use(arrivalLayout);
                    if (arrivalNote is not null) Console.WriteLine($"[gateway] {player.Connection.Remote} {arrivalNote}");
                    Sirus.ConfigureArrival(player);
                    // An area with a Trial of Ascendancy gives the Lord's Labyrinth quest, before the player object carries the flags.
                    if (LabyrinthTrials.For(player.Layout.Id) is not null && !player.Character.HasQuestFlag(Quests.LabyrinthQuestReceived))
                        await SetQuestFlags(player, [Quests.LabyrinthQuestReceived]);
                    await SendPlayer(player);
                    await PlayerEnteredArea(player);
                    // Record the area visit after 0x2bb PlayerObject is sent: the client's CharacterAudioController
                    // (0x140108c80) snapshots the 0x2bb quest flags as its baseline, so the new area's StartQuestFlags
                    // (Visited-*) must transition 0 -> 1 in a subsequent 0x2bc update (sent by SendWaypoints below, or
                    // directly when the waypoint table is absent) for Enter* character voice lines to play once on first entry.
                    bool newlyVisited = RecordVisit(player);
                    await SendWaypoints(player);
                    if (newlyVisited && WaypointCatalog.Areas.Entries.Count == 0)
                        await SendWorldRecord(player, player.Id, [], "0x02bc player quest flags (area visit)",
                            atGate: () => QuestFlagPresentation(player));
                    if (newlyVisited && CharacterAudioCatalog.Default.FirstEntryFor(player.Layout.Id) is { } firstEntry)
                        await Send(player, InstanceNpcs.TextAudio(0, firstEntry.NpcRow, firstEntry.TextAudioRow),
                            $"0x01c7 first-entry NPC audio for {player.Layout.Id} (npc {firstEntry.NpcRow}, line {firstEntry.TextAudioRow})");
                    uint gold = PersonalStashOf(player).Gold;
                    await Send(player, InstanceEntities.Gold(gold), $"0x02aa gold: {gold} (account, {player.Character.League ?? "Standard"})");
                    // The worn cosmetics (S→C 0x1f3): the client's worn table starts empty with every instance connection.
                    await SendWornCosmetics(player, "entering the area");
                    // The saved skill bar (S→C 0x53), right after the player and the area.
                    await SendSkillBar(player);
                    // The equipment again (S→C 0x02bc), as after an equip: the player object's own list does not set how a
                    // two-handed weapon is held, the update does (live 2026-10-22: it was held one-handed after every load).
                    await SendModel(player, Looks(player, ItemsOf(player)), "entering");
                    // The world runs from here: the client shows the player and its area only once the frames start
                    // (S→C 0x2b8 first), so they start before the monsters are placed, which may take a moment, and the
                    // monsters come in while the player already stands there, as a summon or a pack spawned in a fight.
                    ActivateResourceEffects(player, ProjectileWorldTime(player));
                    // Nobody walked while the client was on its loading screen, so the walk history starts here: without this
                    // the first collision pass replayed the whole entry (1.2 s of frames) under the combat lock, 258 ms, and
                    // the player's first movement waited for it (live 2026-11-17).
                    player.WalkersSteppedTo = ProjectileWorldTime(player);
                    StartWorldFrames(player);
                    // Wait until Joining + this player's first frame end before starting periodic updates, so
                    // restored persistent summons and minion rosters never drain to an already-due peer alone.
                    player.Frames.AfterFirstFrame(player, () =>
                    {
                        if (!player.Closed && !player.LeavingArea) Program.ActivatePlayerUpdates(player);
                    });
                    await PopulateArea(player);
                    // The map overlay's "X monsters remain" (S→C 0x1a0), once the whole area is placed and long after S→C
                    // 0x21 activated it (SendPlayer, above): a new instance's byte is 0xff, so without this the line
                    // reads "More than 50 monsters remain" everywhere (InstanceProtocol.MonstersRemaining).
                    await SendRemainingMonsters(player, force: true);
                    await SendGroundItems(player, generates);
                    await PlaceStartWeapon(player);
                    await SpawnTownNpcs(player);
                    if (generates) await SpawnTownStash(player);
                    // All stationary objects must exist before validating the arrival cell.
                    await PushOutOfCollision(player, tell: true);
                    await player.Commands!.Reply($"Area generated at Seed: {player.Seed}");
                    if (tileHashMismatch && player.Commands is { } desyncCommands)
                        await desyncCommands.Reply($"Warning: client terrain hash differs from server generation (client {clientTileHash}, server {serverTileHash}); area layout may be desynchronised.");
                    // An area the Gateway does not generate itself (ServerLevels) runs on the client's layout alone.
                    if (!ServerLevels.IsSupported(player.Layout.Id) && !CaptureTour.Active(CharacterName(player)))
                        await player.Commands!.Reply(ServerLevels.UnsupportedMessage);
                    if (generates) await GameEvents.RunAreaGenerated(player);
                    await EnterAreaFixtures(player);
                    player.Instance.AddPlayer(player);
                    await GameEvents.RunPlayerEntered(player);
                    _ = player.RampageWorld;
                    if (player.OwnedCombatState is RampageWorld world) EnsureRampageWorker(player, world);
                    // Start Grace Period after frames, collision push, and combat runtime setup settle so the origin
                    // matches the final standing cell and AreaBuffs.Set is never cleared during entry.
                    await StartGracePeriod(player);
                    if (!player.Closed && !player.LeavingArea) await GameEvents.RunClientReady(player);
                }
            }
            else if (id == InstanceItems.CurrencyApplicationForesightRequestId)
            {
                // C→S 0x29d: Hinekora's Lock single-item preview request (20 bytes).
                var req = InstanceItems.ReadCurrencyApplicationForesightRequest(bytes);
                await SendSingleForesightPreview(player, req.Key, req.CurrencyInventory, req.CurrencyId, req.TargetInventory, req.TargetId);
            }
            else if (id == InstanceItems.DualCurrencyApplicationForesightRequestId)
            {
                // C→S 0x29e: Hinekora's Lock dual-item preview request (28 bytes).
                var req = InstanceItems.ReadDualCurrencyApplicationForesightRequest(bytes);
                await SendDualForesightPreview(player, req);
            }
            else if (id == InstanceItems.CurrencyTypeApplicationForesightRequestId)
            {
                // C→S 0x29f: Hinekora's Lock Shift-repeat/type preview request (25 bytes).
                var req = InstanceItems.ReadCurrencyTypeApplicationForesightRequest(bytes);
                var (currencyInv, currencyId) = LocateCurrencyStackByType(player, req.CurrencyTypeHash, req.CurrencyInventory);
                await SendSingleForesightPreview(player, req.Key, currencyInv, currencyId, req.TargetInventory, req.TargetId);
            }
        }
        if (player.Closed || player.LeavingArea) return;
        // a1q5: after a read that moved items, the Medicine Chest's flag (and its removal once Nessa has one), here on the
        // message path so the inventory revisions stay in order.
        await GameEvents.RunMessagesHandled(player, [.. messages.Select(m => m.Id)]);
        if (unknown.Length > 0)
        {
            ushort uid = unknown.Length > 1 ? (ushort)(unknown[0] << 8 | unknown[1]) : unknown[0];
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= unframed 0x{uid:x4} ({unknown.Length} bytes): {Convert.ToHexString(unknown.AsSpan(0, Math.Min(unknown.Length, 256)))}");
            // C→S 0x29d: Hinekora's Lock preview request: u16 key, u32 currency inventory, u32 currency id,
            // u32 target inventory, u32 target id (all BE after the key). The 0x2a0 answer uses the same key.
            if (uid == 0x029d && unknown.Length >= 20)
            {
                static uint U32(byte[] b, int i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
                ushort key = (ushort)(unknown[2] << 8 | unknown[3]);
                uint currencyInventory = U32(unknown, 4), currencyId = U32(unknown, 8);
                uint targetInventory = U32(unknown, 12), targetId = U32(unknown, 16);
                var items = ItemsOf(player);
                var stash = StashOf(player);
                var (usedSpot, currency) = Locate(items, stash, currencyInventory, currencyId);
                var (targetSpot, target) = Locate(items, stash, targetInventory, targetId);
                Item? rawPreview = currency is not null && target is not null && target.Item.Foreseeing
                    && !(usedSpot.Tab is int pubTab && stash.IsPublic(pubTab))
                    && !RefusedOnWorn(player, currency.Item, targetSpot)
                    ? CurrencyResult(player, target.Item, currency.Item.TypeHash,
                        ItemRolls.ForeseeRandom(target.Item, currency.Item.TypeHash))
                    : null;
                Item? preview = rawPreview is null ? null : ItemRolls.ClearForesee(rawPreview);
                player.ForesightRequest = (key, currencyInventory, currencyId, targetInventory, targetId,
                    target?.Item.ForeseeSeed ?? 0, preview);
                await Send(player, InstanceItems.CurrencyApplicationForesightResult(
                    key, preview is null ? InstanceItems.CurrencyApplicationForesightFailure : (ushort)0,
                    preview is null ? [] : [preview]),
                    $"0x02a0 foresight preview key {key} for item {targetId}: {(preview is null ? "refused" : "preview")}");
            }
            // Unframed messages have the hooks too.
            if (GameEvents.WantsPacket(uid)) await GameEvents.AnyPacket(player, uid, unknown);
        }
    }

    static Item? ComputeForesightPreview(Player player, ItemMoves.Spot usedSpot, InventoryItem? currency, ItemMoves.Spot targetSpot, InventoryItem? target, AccountStash stash)
    {
        if (currency is null || target is null || !target.Item.Foreseeing) return null;
        if (usedSpot.Tab is int pubTab && stash.IsPublic(pubTab)) return null;
        if (RefusedOnWorn(player, currency.Item, targetSpot)) return null;
        Item? raw = CurrencyResult(player, target.Item, currency.Item.TypeHash,
            ItemRolls.ForeseeRandom(target.Item, currency.Item.TypeHash));
        return raw is null ? null : ItemRolls.ClearForesee(raw);
    }

    public static (uint InventoryId, uint ItemId) LocateCurrencyStackByType(Player player, uint typeHash, uint preferredInventoryId)
    {
        if (player.Closed || player.LeavingArea) return (0, 0);
        var items = ItemsOf(player);
        var stash = StashOf(player);
        bool stashAllowed = player.StashOpen || player.GuildStashOpen;
        bool expandedAllowed = ExpandedInventoryGranted(player);
        if (preferredInventoryId != 0)
        {
            if (stash.TabOf(preferredInventoryId) is int prefTab)
            {
                if (stashAllowed && !stash.IsPublic(prefTab)
                    && stash.Inventory(prefTab).Items.FirstOrDefault(i => i.Item.TypeHash == typeHash) is { } prefStashHit)
                    return (preferredInventoryId, prefStashHit.Id);
            }
            else if (PlayerInventories.Row(preferredInventoryId) is byte prefRow
                && (prefRow == PlayerInventories.MainInventory || (expandedAllowed && prefRow == PlayerInventories.ExpandedInventory))
                && items.Items(prefRow).FirstOrDefault(i => i.Item.TypeHash == typeHash) is { } prefInvHit)
                return (preferredInventoryId, prefInvHit.Id);
        }
        byte[] rows = expandedAllowed
            ? [PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory]
            : [PlayerInventories.MainInventory];
        foreach (byte row in rows)
            if (items.Items(row).FirstOrDefault(i => i.Item.TypeHash == typeHash) is { } hit)
                return (PlayerInventories.InventoryId(row), hit.Id);
        if (stashAllowed)
            for (int t = 0; t < stash.Count; t++)
                if (!stash.IsPublic(t) && stash.Inventory(t).Items.FirstOrDefault(i => i.Item.TypeHash == typeHash) is { } hit)
                    return (stash.InventoryOf(t), hit.Id);
        return (0, 0);
    }

    static async Task SendSingleForesightPreview(Player player, ushort key, uint currencyInventory, uint currencyId,
        uint targetInventory, uint targetId)
    {
        if (player.Closed || player.LeavingArea) return;
        var items = ItemsOf(player);
        var stash = StashOf(player);
        var (usedSpot, currency) = currencyId != 0 ? Locate(items, stash, currencyInventory, currencyId) : default;
        var (targetSpot, target) = Locate(items, stash, targetInventory, targetId);
        Item? preview = ComputeForesightPreview(player, usedSpot, currency, targetSpot, target, stash);
        player.ForesightRequest = (key, currencyInventory, currencyId, targetInventory, targetId,
            target?.Item.ForeseeSeed ?? 0, preview);
        await Send(player, InstanceItems.CurrencyApplicationForesightResult(
            key, preview is null ? InstanceItems.CurrencyApplicationForesightFailure : (ushort)0,
            preview is null ? [] : [preview]),
            $"0x02a0 foresight preview key {key} for item {targetId}: {(preview is null ? "refused" : "preview")}");
    }

    static async Task SendDualForesightPreview(Player player, InstanceItems.DualCurrencyApplicationForesightRequest req)
    {
        if (player.Closed || player.LeavingArea) return;
        var items = ItemsOf(player);
        var stash = StashOf(player);
        var (usedSpot, currency) = Locate(items, stash, req.CurrencyInventory, req.CurrencyId);
        var (firstSpot, first) = Locate(items, stash, req.FirstTargetInventory, req.FirstTargetId);
        var (secondSpot, second) = Locate(items, stash, req.SecondTargetInventory, req.SecondTargetId);
        var previews = new List<Item>(2);
        Item? firstPreview = ComputeForesightPreview(player, usedSpot, currency, firstSpot, first, stash);
        if (firstPreview is not null) previews.Add(firstPreview);
        if (req.SecondTargetId != 0 && (req.SecondTargetId != req.FirstTargetId || req.SecondTargetInventory != req.FirstTargetInventory))
        {
            Item? secondPreview = ComputeForesightPreview(player, usedSpot, currency, secondSpot, second, stash);
            if (secondPreview is not null) previews.Add(secondPreview);
        }
        Item? primary = firstPreview ?? previews.FirstOrDefault();
        InventoryItem? primaryTarget = firstPreview is not null ? first : second;
        uint primaryInv = firstPreview is not null ? req.FirstTargetInventory : req.SecondTargetInventory;
        uint primaryId = firstPreview is not null ? req.FirstTargetId : req.SecondTargetId;
        player.ForesightRequest = (req.Key, req.CurrencyInventory, req.CurrencyId, primaryInv, primaryId,
            primaryTarget?.Item.ForeseeSeed ?? 0, primary);
        await Send(player, InstanceItems.CurrencyApplicationForesightResult(
            req.Key, previews.Count == 0 ? InstanceItems.CurrencyApplicationForesightFailure : (ushort)0, previews),
            $"0x02a0 dual foresight preview key {req.Key} for items {req.FirstTargetId}/{req.SecondTargetId}: {(previews.Count == 0 ? "refused" : $"{previews.Count} preview(s)")}");
    }

    static async Task FinishCharacterSelectExit(Player player)
    {
        if (!player.PendingCharacterSelectExit || player.Closed) return;
        player.PendingCharacterSelectExit = false;
        Console.WriteLine($"[gateway] {player.Connection.Remote} finishing exit to character selection: flushing progress and sending clean 0x0026");
        FlushAreaProgress(player);
        await Send(player, InstanceProtocol.Disconnect(InstanceProtocol.DisconnectCleanCode), "0x0026 clean disconnect (character select)");
        player.Connection.CloseReason = "exit to character selection (0x0153)";
        player.Closed = true;
        player.InvalidateIncomingHits();
    }
}
