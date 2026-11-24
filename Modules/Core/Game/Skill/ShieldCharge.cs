using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    // Shield Charge's movement (11-skills.md "Shield Charge"). The client moves the charge at Pathfinding::GetSpeed(player)
    // × (1 + combined_off_hand_attack_speed_+% / 100) × max(0, 1 + combined_off_hand_attack_speed_+%_final / 100), capped
    // by movement_velocity_cap (0x141a70760). The gem adds base_movement_velocity_+% through the invisible buff
    // shield_charge_movement_speed_while_charging (BuffDefinitions 1352) while charging; network stats run no calculators,
    // so the server adds that bonus into movement_velocity_+% and publishes it into Area.Stats on the exact frame the
    // charge starts and ends. Targets closer than 24 cells (dx²+dy² < 0x240, 0x141a6ee79) do not charge at all and instead
    // perform the in-place end attack with +30% attack time (shield_charge_attack_time_+30%_if_no_charge, 0x2720).
    sealed class ShieldChargeRun
    {
        public int Percent, Token, ActionToken;
        public double AttackSpeedMultiplier = 1, EndAttackSeconds;
        public bool Traveling, Arrived, Interrupted, EndCompleted;
        public (int X, int Y) Start, End, ArrivedAtCell;
        public double StartedAt, ArrivedAt, MaxDistance, LastHitDist;
        public long LengthSq;
        public uint TargetObjectId;
        public bool ContinuePastTarget;
        public int KnockbackSpeedPct;
        public int PathRadius, EndRadius, WaveRadius;
        public readonly HashSet<uint> PushedMonsters = [];
    }
    static readonly Store<Player, ShieldChargeRun> ShieldChargeRuns = new();

    /// <summary>The minimum straight-line distance in grid cells required to enter Shield Charge's travel phase (0x141a6ee79).</summary>
    public const int ShieldChargeMinDistance = 24;

    public static bool IsShieldChargeActive(Player player) =>
        ShieldChargeRuns.TryGetValue(player, out var run) && (run.Traveling || run.Percent != 0);

    public static void RefreshShieldChargeArrival(Player player)
    {
        if (ShieldChargeRuns.TryGetValue(player, out var run) && run.Traveling && !run.Interrupted && PlayerActionCurrent(player, run.ActionToken))
            player.NextAttackAt = player.Walk.ArrivesAt + run.EndAttackSeconds;
    }

    // Client step collision for Shield Charge (FUN_141a6f9b0): passes through non-target Actors and Chests (pushing
    // hostile knockbackable enemies perpendicularly aside at 60 × (1 + knockback_speed_+%/100)), stops on terrain,
    // stationary non-Actor/non-Chest blockers, and an object-targeted monster at ≥ 15 cells (dist² > 0xe0).
    public static bool ShieldChargeStepBlocked(Player owner, uint id, GameServer.Core.World.PlayerWalk walk, double at,
        (int X, int Y) cell, IReadOnlyList<GameServer.Core.World.ClientPathfinder.Blocker>? stationary = null)
    {
        Player? p = id == owner.Id ? owner : owner.Frames.Players().FirstOrDefault(x => x.Id == id);
        if (p is null || !ShieldChargeRuns.TryGetValue(p, out var run) || !run.Traveling || run.Interrupted
            || !PlayerActionCurrent(p, run.ActionToken))
            return false;
        stationary ??= p.SceneryBlockers;
        bool blocked = GameServer.Core.World.ClientPathfinder.ObjectBlocks(stationary, cell.X, cell.Y, walk.Size + 1, walk.Size + 4);
        if (blocked && !walk.WalksThrough) return true;
        if (!blocked) walk.WalksThrough = false;
        long sdx = cell.X - run.Start.X, sdy = cell.Y - run.Start.Y;
        bool past15 = sdx * sdx + sdy * sdy > 0xe0;
        if (run.TargetObjectId != 0 && !run.ContinuePastTarget && past15
            && owner.Area.Monsters.TryGetValue(run.TargetObjectId, out var tm) && !tm.Dead && tm.Life > 0)
        {
            var tPos = tm.Walk.Position(at);
            int tSize = LivingCollision.Size(tm);
            if (Math.Max(Math.Abs(cell.X - tPos.X), Math.Abs(cell.Y - tPos.Y)) - tSize < walk.Size + 1)
                return true;
        }
        if (run.LengthSq > 100)
        {
            double vx = run.End.X - run.Start.X, vy = run.End.Y - run.Start.Y;
            double segLen = Math.Sqrt(run.LengthSq);
            foreach (var (mId, m) in owner.Area.MonstersNear(cell, walk.Size + 12))
            {
                if ((mId == run.TargetObjectId && past15) || run.PushedMonsters.Contains(mId)
                    || !KnockbackAllowed(m) || !LivingCollision.Blocks(m)) continue;
                var mPos = m.Walk.Position(at);
                int mSize = LivingCollision.Size(m);
                if (Math.Max(Math.Abs(cell.X - mPos.X), Math.Abs(cell.Y - mPos.Y)) - mSize >= walk.Size + 1) continue;
                double t = Math.Clamp(((mPos.X - run.Start.X) * vx + (mPos.Y - run.Start.Y) * vy) / run.LengthSq, 0.0, 1.0);
                double cx = run.Start.X + t * vx, cy = run.Start.Y + t * vy;
                double ox = mPos.X - cx, oy = mPos.Y - cy;
                double perpDist = Math.Sqrt(ox * ox + oy * oy);
                double want = mSize + 6 + walk.Size;
                if (perpDist >= want) continue;
                double px, py;
                if (perpDist > 1e-4) (px, py) = (ox / perpDist, oy / perpDist);
                else (px, py) = (-vy / segLen, vx / segLen);
                var pushTo = mPos;
                int maxSteps = (int)Math.Ceiling(want - perpDist);
                for (int step = 1; step <= maxSteps; step++)
                {
                    int nx = (int)Math.Round(mPos.X + px * step);
                    int ny = (int)Math.Round(mPos.Y + py * step);
                    if ((nx, ny) == pushTo) continue;
                    if (!owner.Layout.WalkableFor(nx, ny, mSize) || owner.Barrier.Contains((nx, ny))) break;
                    pushTo = (nx, ny);
                }
                if (pushTo != mPos)
                {
                    double kbSpeed = 60.0 * Math.Max(0.1, 1.0 + run.KnockbackSpeedPct / 100.0);
                    m.Walk.Slide(pushTo.X, pushTo.Y, Dist(mPos, pushTo) / kbSpeed, at);
                    run.PushedMonsters.Add(mId);
                }
            }
        }
        return false;
    }

    // The running charge's movement bonus, for the pools (PoolsFor); 0 when none runs.
    public static int ShieldChargeMovementPct(Player player) =>
        ShieldChargeRuns.TryGetValue(player, out var run) && run.Traveling && !run.Interrupted && PlayerActionCurrent(player, run.ActionToken)
            ? run.Percent : 0;

    // The running charge's off-hand attack-speed multiplier for Player.ApplyMovementSpeedAt (0x141a70760); 1 when none runs.
    public static double MovementSpeedMultiplier(Player player) =>
        ShieldChargeRuns.TryGetValue(player, out var run) && run.Traveling && !run.Interrupted && PlayerActionCurrent(player, run.ActionToken)
            ? run.AttackSpeedMultiplier : 1;

    // A movement skill that needs a shield and carries its own movement bonus: it walks there faster, it does not blink.
    public static int ShieldChargeBonusPct(Player player, ushort skillId, int effect) =>
        SkillsFor(player).IsMovement(effect) && SkillsFor(player).RequiresShield(effect)
            && Stat.BaseMovementVelocityPlusPct is var key ? SupportedSkill(player, skillId).Stats.GetValueOrDefault(key) : 0;

    // The client's setup 0x141a6e4f0 stops a charge aimed further away at 75 cells × (1 + shield_charge_range_+% / 100)
    // from where it starts (integer, (100 + range) * 75 / 100), along the aim: the "Maximum Charge Distance" of the gem's
    // damage line, then adds shield_charge_extra_distance (0x1d2) and raycasts forward along walkable cells (0x141a7b120).
    const int ShieldChargeBaseDistance = 75;

    public static int ShieldChargeMaxDistance(Player player, ushort skillId) =>
        (100 + (Stat.ShieldChargeRangePlusPct is var k ? SupportedStats(player, skillId).GetValueOrDefault(k) : 0))
            * ShieldChargeBaseDistance / 100;

    public static double ShieldChargeAttackSpeedMultiplier(Player player, ushort skillId)
    {
        if (ShieldWeapon(player, skillId) is not { } shieldWeapon) return 1;
        var (_, incPct, morePct, _) = SupportedOffHandAttackSpeed(player, skillId, shieldWeapon);
        return Math.Max(0.01, (1f + incPct / 100f) * MathF.Max(0f, 1f + morePct / 100f));
    }

    public static double ShieldChargeEndAttackSeconds(Player player, ushort skillId, bool noCharge)
    {
        // The minimum is one complete action per frame, including the in-place bash's duration modifier.
        double seconds = SupportedAttackSecondsFor(player, skillId, ShieldWeapon(player, skillId), clampToTick: false);
        if (noCharge && SupportedStats(player, skillId).GetValueOrDefault(Stat.ShieldChargeAttackTimePlus30PctIfNoCharge) != 0)
            seconds *= 1.3;
        return Math.Max(Program.TickSeconds, seconds);
    }

    static void SyncShieldChargeMovementStat(Player player, double at)
    {
        player.Pools = PoolsFor(player, player.Pools.Level);
        int publishedPct = player.Pools.MovementVelocityPct + player.Rampage.Current.MovementPercent;
        // Publish into Area.Stats immediately so the 0x1b3 movement-speed update ships in the same frame as Shield Charge's
        // 0x1ab start/end for both Lockstep and Predictive clients.
        if (player.Frames.Running && player.Area.Stats.HasWritten(player.Id))
            player.Area.Stats.Set(player.Id, Stat.MovementVelocityPlusPct, publishedPct);
        player.ApplyMovementSpeedAt(at, forceRelay: true, movementPctOverride: publishedPct);
    }

    static void EndShieldChargeMovement(Player player, ShieldChargeRun run, double? arrivedAt = null)
    {
        if (!run.Traveling && run.Percent == 0 && !player.Walk.ChargesThrough) return;
        run.Traveling = false;
        run.Percent = 0;
        run.AttackSpeedMultiplier = 1;
        player.Walk.ChargesThrough = false;
        if (arrivedAt is double at && run.EndAttackSeconds > 0)
            player.NextAttackAt = Math.Min(player.NextAttackAt, at + run.EndAttackSeconds);
        else if (arrivedAt is null)
            player.NextAttackAt = Math.Min(player.NextAttackAt, AreaTime(player));
        SyncShieldChargeMovementStat(player, arrivedAt ?? AreaTime(player));
    }

    /// <summary>Whether Shield Charge is still in its un-cancellable travel phase (+0x3e5 != 0, 0x141a6fd50) at
    /// <paramref name="at"/>. Clears the charge movement bonus on arrival or interruption.</summary>
    public static bool ShieldChargeTraveling(Player player, double at)
    {
        if (!ShieldChargeRuns.TryGetValue(player, out var run) || !run.Traveling) return false;
        if (run.Interrupted || !PlayerActionCurrent(player, run.ActionToken) || player.Closed || player.PlayerLife == 0)
        {
            EndShieldChargeMovement(player, run);
            return false;
        }
        if (at < player.Walk.ArrivesAt - 1e-9) return true;
        run.Arrived = true;
        run.ArrivedAt = player.Walk.ArrivesAt;
        run.ArrivedAtCell = player.Walk.Position(player.Walk.ArrivesAt);
        EndShieldChargeMovement(player, run, player.Walk.ArrivesAt);
        return false;
    }

    // A queued attack may replace the action token before the frame's delayed hit jobs run.
    // Remember which end animation had already finished on the action clock.
    static void AdvanceShieldChargeForUse(Player player, double at, bool attack)
    {
        ShieldChargeTraveling(player, at);
        if (!ShieldChargeRuns.TryGetValue(player, out var run) || run.Traveling || run.Interrupted
            || !PlayerActionCurrent(player, run.ActionToken)) return;
        double nextStart = LockstepScheduledAt(player, at);
        if (attack) nextStart = Math.Max(nextStart, player.NextAttackAt);
        double end = (run.Arrived ? run.ArrivedAt : run.StartedAt) + run.EndAttackSeconds;
        run.EndCompleted = nextStart + 1e-9 >= end;
    }

    public static void EndShieldChargeIfInterrupted(Player player)
    {
        if (!ShieldChargeRuns.TryGetValue(player, out var run)) return;
        if (!run.Arrived && !run.EndCompleted) run.Interrupted = true;
        if (run.Traveling || run.Percent != 0 || player.Walk.ChargesThrough)
            EndShieldChargeMovement(player, run);
    }

    // At paid release: the bonus on, the pools and walk speed with it, and the walk to the aimed cell, no further
    // than the charge's maximum distance. Returns the run's token, which only the latest charge's end may clear.
    public static int BeginShieldCharge(Player player, ushort skillId, int percent, int x, int y, double at) =>
        BeginShieldCharge(player, skillId, percent, x, y, at, player.ActionCount, out _);

    public static int BeginShieldCharge(Player player, ushort skillId, int percent, int x, int y, double at, int actionToken, out bool charged) =>
        BeginShieldCharge(player, skillId, percent, x, y, at, actionToken, out charged, targetObjectId: 0, previousNextAttackAt: null);

    public static int BeginShieldCharge(Player player, ushort skillId, int percent, int x, int y, double at, int actionToken, out bool charged,
        uint targetObjectId = 0, double? previousNextAttackAt = null)
    {
        bool hadBonus = player.Walk.ChargesThrough;
        int nextToken = 1;
        if (ShieldChargeRuns.TryGetValue(player, out var previous))
        {
            hadBonus |= previous.Traveling || previous.Percent != 0;
            nextToken = previous.Token + 1;
        }
        var run = new ShieldChargeRun { Token = nextToken };
        ShieldChargeRuns.AddOrUpdate(player, run);
        var supported = SupportedSkill(player, skillId);
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
        int effect = gem.Name is not null ? gem.Effect : supported.Effect;
        int max = ShieldChargeMaxDistance(player, skillId);
        int extra = supported.Stats.GetValueOrDefault(Stat.ShieldChargeExtraDistance);
        double attackMult = ShieldChargeAttackSpeedMultiplier(player, skillId);
        double startAt = LockstepScheduledAt(player, at);
        run.StartedAt = startAt;
        run.Start = player.Walk.Position(startAt);
        run.MaxDistance = Math.Max(1, max);
        run.ActionToken = actionToken;
        run.TargetObjectId = targetObjectId;
        run.ContinuePastTarget = supported.Stats.GetValueOrDefault(Stat.ShieldChargeContinuePastTarget) != 0;
        run.KnockbackSpeedPct = CharacterPlayerStats(player).GetValueOrDefault(Stat.KnockbackSpeedPlusPct);
        run.PathRadius = Math.Max(1, SkillRadius(player, ShieldChargePathRadius, supported.Stats, spell: false, effect));
        run.EndRadius = Math.Max(1, SkillRadius(player, ShieldChargeEndRadius, supported.Stats, spell: false, effect));
        run.WaveRadius = Math.Max(1, SkillRadius(player, ShieldChargeWaveRadius, supported.Stats, spell: false, effect));
        player.Walk.Charge(x, y, max, startAt, extra, ShieldChargeMinDistance, out charged);
        if (!charged)
        {
            run.End = (x, y);
            run.LengthSq = 0;
            player.Walk.ChargesThrough = false;
            double endSeconds = ShieldChargeEndAttackSeconds(player, skillId, noCharge: true);
            run.EndAttackSeconds = endSeconds;
            if (hadBonus) SyncShieldChargeMovementStat(player, startAt);
            double baseNext = previousNextAttackAt ?? player.NextAttackAt;
            player.NextAttackAt = AttackTiming.After(startAt, baseNext, endSeconds);
            if (player.Connection.LockstepClient)
            {
                double frameEnd = player.Connection.LockstepFrame?.End ?? startAt;
                string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
                double cutIn = endSeconds * Animations.AttackCutIn(ao, AnimationWeaponProfileFor(player), "bash1");
                player.LockstepBusyUntil = Math.Max(player.LockstepBusyUntil, Math.Min(startAt + endSeconds, frameEnd + cutIn));
            }
            return run.Token;
        }
        run.End = player.Walk.Destination;
        long ldx = run.End.X - run.Start.X, ldy = run.End.Y - run.Start.Y;
        run.LengthSq = ldx * ldx + ldy * ldy;
        run.Percent = percent;
        run.Traveling = true;
        run.AttackSpeedMultiplier = attackMult;
        run.EndAttackSeconds = ShieldChargeEndAttackSeconds(player, skillId, noCharge: false);
        player.Walk.ChargesThrough = true;
        SyncShieldChargeMovementStat(player, startAt);
        player.NextAttackAt = Math.Max(previousNextAttackAt ?? player.NextAttackAt, player.Walk.ArrivesAt + run.EndAttackSeconds);
        return run.Token;
    }

    // A shield skill's attack swings the shield (Shield Charge, Shield Crush), which has no weapon damage: the gem's own
    // off_hand_local_minimum/maximum_added_physical_damage plus its added damage per 15 of the shield's armour and evasion
    // rating (the item's own, local values: "per 15 Armour or Evasion Rating on Shield"), at the gem's AttackTime. Null
    // for other skills and without a shield, whose attacks use the main hand's weapon.
    public static SkillCatalog.Unarmed? ShieldWeapon(Player player, ushort skillId)
    {
        var gem = SupportedSkill(player, skillId);
        if (!SkillsFor(player).IsAttack(gem.Effect) || !SkillsFor(player).RequiresShield(gem.Effect)
            || ActiveOffHand(player)?.Type?.Class != "Shield") return null;
        int Own(uint k) => gem.Stats.GetValueOrDefault(k);
        var shield = EquippedArmourDefences(player).FirstOrDefault(d => d.Row == PlayerInventories.Offhand1).Defence;
        int per15 = (int)((shield.Armour + shield.Evasion) / 15);
        int min = Own(Stat.OffHandLocalMinimumAddedPhysicalDamage)
            + per15 * Own(Stat.OffHandMinimumAddedPhysicalDamagePer15ShieldArmourAndEvasionRating);
        int max = Own(Stat.OffHandLocalMaximumAddedPhysicalDamage)
            + per15 * Own(Stat.OffHandMaximumAddedPhysicalDamagePer15ShieldArmourAndEvasionRating);
        int attackMs = SkillsFor(player).AttackTimeMs(gem.Effect, gem.Level);
        var weapon = new SkillCatalog.Unarmed(attackMs > 0 ? attackMs : WeaponFor(player).AttackMs, min, Math.Max(min, max), WeaponFor(player).Range);
        int per15Evasion = (int)(shield.Evasion / 15);
        int coldMin = Own(Stat.OffHandLocalMinimumAddedColdDamage)
            + per15Evasion * Own(Stat.OffHandMinimumAddedColdDamagePer15ShieldEvasion);
        int coldMax = Own(Stat.OffHandLocalMaximumAddedColdDamage)
            + per15Evasion * Own(Stat.OffHandMaximumAddedColdDamagePer15ShieldEvasion);
        return coldMax <= 0 ? weapon : weapon with
        {
            LocalDamage = DamagePacket.Of(DamageType.Physical, min, Math.Max(min, max))
                .Add(DamageType.Cold, new(coldMin, Math.Max(coldMin, coldMax))),
        };
    }

    // The client's charge hits (FUN_141a6ea70:75-92, 0x141a70170): Kind 0 path hits triggered by distance traveled
    // (every PathRadius cells along the charge, base 8, each monster at most once per charge), Kind 1 arrival hit
    // within EndRadius (base 16, reaching every monster there), and Kind 2 forward 55° cone wave up to WaveRadius
    // (base 28, hitting monsters not already hit by Kind 1).
    const double ShieldChargePollSeconds = 0.016;
    const double ShieldChargeConeCos = 0.573576436351046; // cos(55°)
    const int ShieldChargePathRadius = 8, ShieldChargeEndRadius = 16, ShieldChargeWaveRadius = 28;

    // After the use's echo: distance-driven path hits while the server's walk runs, the arrival hit + forward cone wave
    // where it arrives, and guaranteed speed cleanup on interruption (a later charge takes over with its own run).
    public static Task RunShieldCharge(Player player,
        int token, double arrivesAt, ushort skillId, ushort sequence, int actionToken, CooldownUseTicket? cooldownUse, bool cryExerted) =>
        RunShieldCharge(player, token, charged: true, skillId, sequence, actionToken, cooldownUse, cryExerted);

    public static Task RunShieldCharge(Player player,
        int token, bool charged, ushort skillId, ushort sequence, int actionToken, CooldownUseTicket? cooldownUse, bool cryExerted) =>
        RunShieldChargeHits(player, token, charged, skillId, sequence, actionToken, cooldownUse, cryExerted, null);

    static async Task RunShieldChargeHits(Player player,
        int token, bool charged, ushort skillId, ushort sequence, int actionToken, CooldownUseTicket? cooldownUse, bool cryExerted, SkillCriticalRoll? criticalRoll)
    {
        if (!ShieldChargeRuns.TryGetValue(player, out var run) || run.Token != token) return;
        int epoch = player.ProjectileEpoch, interruption = player.ActionInterruptionGeneration;
        bool Current() => !run.Interrupted && !player.LeavingArea && !player.Closed && player.PlayerLife > 0
            && player.ProjectileEpoch == epoch && player.ActionInterruptionGeneration == interruption
            && (run.EndCompleted || run.Arrived || (ShieldChargeRuns.TryGetValue(player, out var cur) && ReferenceEquals(cur, run)
                && PlayerActionCurrent(player, actionToken)));
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
        var skillStats = SupportedStats(player, skillId);
        int maximumMore = skillStats.TryGetValue(Stat.ShieldChargeDamagePlusPctMaximum, out int dmgMax) ? dmgMax : 100;
        int maxStunReduction = skillStats.TryGetValue(Stat.ShieldChargeScalingStunThresholdReductionPlusPctAtMaximumRange, out int stunRedMax) ? stunRedMax : 75;
        int maxStunDuration = skillStats.GetValueOrDefault(Stat.ShieldChargeStunDurationPlusPctMaximum);
        int maxKnockbackDist = skillStats.GetValueOrDefault(Stat.ShieldChargeKnockbackDistancePlusPctMaximum);
        bool removeStunAndKnockback = skillStats.GetValueOrDefault(Stat.ShieldChargeRemoveStunAndKnockback) != 0;
        int damagePerTargetPct = skillStats.GetValueOrDefault(Stat.ShieldChargeDamagePerTargetHitPlusPct);
        if (ShieldWeapon(player, skillId) is { } shieldWeapon)
            Console.WriteLine($"[gateway] {player.Connection.Remote} Shield Charge damage: shield base {shieldWeapon.MinDamage}-{shieldWeapon.MaxDamage}, " +
                $"up to {maximumMore}% more at {run.MaxDistance:0} cells{(charged ? "" : " (no-charge)")}");
        criticalRoll ??= CaptureSkillCriticalRoll(player, skillId);
        double ruthless = RuthlessBlowMore(player, skillId, id => skillStats.GetValueOrDefault(id));
        var pathHit = new HashSet<uint>();
        Task Hit((int X, int Y) at, int radius, ISet<uint>? once, int? hitAction, double distForScale,
            Func<uint, (int X, int Y), bool>? targetFilter = null)
        {
            if (gem.Name is null) return Task.CompletedTask;
            double frac = run.MaxDistance > 0 ? Math.Clamp(distForScale / run.MaxDistance, 0.0, 1.0) : 0.0;
            double more = 1.0 + maximumMore / 100.0 * frac;
            if (damagePerTargetPct != 0 && pathHit.Count > 0)
                more *= Math.Max(0.0, 1.0 + damagePerTargetPct * pathHit.Count / 100.0);
            int stunRed = (int)Math.Round(maxStunReduction * frac);
            int stunDur = (int)Math.Round(maxStunDuration * frac);
            int kbDist = (int)Math.Round(maxKnockbackDist * frac);
            return AreaSkillHitForUse(ruthless, player, skillId, gem, at, at, immediate: true, sequence: sequence,
                actionToken: hitAction, cooldownUse: hitAction is null ? null : cooldownUse,
                damageMore: more, criticalRoll: criticalRoll, radiusOverride: radius, once: once, cryExerted: cryExerted,
                targetFilter: targetFilter, extraStunThresholdReduction: stunRed,
                extraStunDurationPct: stunDur, extraKnockbackDistancePct: kbDist,
                removeStunAndKnockback: removeStunAndKnockback, offHandWeapon: true);
        }
        Task HitCone((int X, int Y) origin, (int X, int Y) dirFrom, (int X, int Y) dirTo, int radius, ISet<uint> once,
            int? hitAction, double distForScale)
        {
            double dx = dirTo.X - dirFrom.X, dy = dirTo.Y - dirFrom.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 1e-6) return Task.CompletedTask;
            double fx = dx / len, fy = dy / len;
            bool InCone(uint _, (int X, int Y) mPos)
            {
                double mx = mPos.X - origin.X, my = mPos.Y - origin.Y;
                double dSq = mx * mx + my * my;
                if (dSq <= 16.0) return true;
                double d = Math.Sqrt(dSq);
                return (mx * fx + my * fy) / d >= ShieldChargeConeCos;
            }
            return Hit(origin, radius, once, hitAction, distForScale, InCone);
        }
        async Task EndWave((int X, int Y) origin, double starts, HashSet<uint> endHit, double distance)
        {
            double left = starts + run.EndAttackSeconds - AreaTime(player);
            if (left > 0) await AreaDelay(player, left);
            if (Current() && (run.EndCompleted || PlayerActionCurrent(player, actionToken)))
                await HitCone(origin, run.Start, run.End, run.WaveRadius, endHit, run.EndCompleted ? null : actionToken, distance);
        }
        if (!charged)
        {
            string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
            double cutIn = run.EndAttackSeconds * Animations.AttackCutIn(ao, AnimationWeaponProfileFor(player), "bash1");
            if (cutIn > 0)
            {
                await AreaDelay(player, cutIn);
                await WhilePaused(player);
            }
            if (!Current()) return;
            var at = run.Start;
            var endHit = new HashSet<uint>();
            await Hit(at, run.EndRadius, endHit, run.EndCompleted ? null : actionToken, distForScale: 0);
            await EndWave(at, run.StartedAt, endHit, 0);
            return;
        }
        try
        {
            if (!player.Frames.Running)
                await SendPlayerStatSnapshot(player, () => PlayerStatsOffFrames(player),
                    $"0x01b3 player stats (Shield Charge starts: movement_velocity_+% {player.Pools.MovementVelocityPct}, " +
                    $"+{ShieldChargeMovementPct(player)} while charging)");
            else
            {
                // Let the frame send the accepted action start before publishing its hits.
                await AreaDelay(player, 0);
                await WhilePaused(player);
            }
            if (!Current()) return;
            await Hit(run.Start, run.PathRadius, pathHit, run.Arrived ? null : actionToken, distForScale: 0);
            double segLen = run.LengthSq > 0 ? Math.Sqrt(run.LengthSq) : 0;
            while ((run.Traveling || run.Arrived) && Current())
            {
                double now = AreaTime(player);
                var pos = run.Arrived ? run.ArrivedAtCell : player.Walk.Position(now);
                double traveled = Dist(run.Start, pos);
                while (traveled - run.LastHitDist >= run.PathRadius && Current())
                {
                    run.LastHitDist += run.PathRadius;
                    var hitAt = segLen > 0
                        ? ((int)Math.Round(run.Start.X + (run.End.X - run.Start.X) * (run.LastHitDist / segLen)),
                           (int)Math.Round(run.Start.Y + (run.End.Y - run.Start.Y) * (run.LastHitDist / segLen)))
                        : pos;
                    await Hit(hitAt, run.PathRadius, pathHit, run.Arrived ? null : actionToken, distForScale: run.LastHitDist);
                }
                if (!run.Traveling || run.Arrived || !Current()) break;
                double left = player.Walk.ArrivesAt - AreaTime(player);
                if (left <= 1e-6) break;
                await AreaDelay(player, Math.Min(ShieldChargePollSeconds, left));
                await WhilePaused(player);
            }
            if (!Current()) return;
            if (run.Arrived || AreaTime(player) >= player.Walk.ArrivesAt - 1e-6)
            {
                double arrivedAt = run.Arrived ? run.ArrivedAt : player.Walk.ArrivesAt;
                var endAt = run.Arrived ? run.ArrivedAtCell : player.Walk.Position(arrivedAt);
                double endDist = Dist(run.Start, endAt);
                if (ShieldChargeRuns.TryGetValue(player, out var active) && ReferenceEquals(active, run))
                    ShieldChargeTraveling(player, arrivedAt);
                var endHit = new HashSet<uint>();
                // On Lockstep a Move can immediately cancel charge_*_end on the arrival frame, after the client already
                // delivered the arrival hit (0x141a70910); when run.Arrived is true, still land Kind 1 at the arrival cell.
                await Hit(endAt, run.EndRadius, endHit, run.Arrived ? null : actionToken, distForScale: endDist);

                if (!player.Connection.LockstepClient)
                    ResyncAfter(player, 0.05, gem.Name ?? "Shield Charge");
                if (!player.Frames.Running)
                    await SendPlayerStatSnapshot(player, () => PlayerStatsOffFrames(player),
                        $"0x01b3 player stats (Shield Charge ends: movement_velocity_+% {player.Pools.MovementVelocityPct})");
                await EndWave(endAt, arrivedAt, endHit, endDist);
            }
        }
        finally
        {
            if (run.Traveling && ShieldChargeRuns.TryGetValue(player, out var cur) && ReferenceEquals(cur, run))
                EndShieldChargeMovement(player, run);
        }
    }
}
