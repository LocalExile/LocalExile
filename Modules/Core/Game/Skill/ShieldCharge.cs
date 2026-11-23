using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Login;

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
        public bool Traveling, Arrived;
        public (int X, int Y) Start, ArrivedAtCell;
        public double MaxDistance;
    }
    static readonly Store<Player, ShieldChargeRun> ShieldChargeRuns = new();

    /// <summary>The minimum straight-line distance in grid cells required to enter Shield Charge's travel phase (0x141a6ee79).</summary>
    public const int ShieldChargeMinDistance = 24;

    // The running charge's movement bonus, for the pools (PoolsFor); 0 when none runs.
    public static int ShieldChargeMovementPct(Player player) =>
        ShieldChargeRuns.TryGetValue(player, out var run) && run.Traveling && PlayerActionCurrent(player, run.ActionToken)
            ? run.Percent : 0;

    // The running charge's off-hand attack-speed multiplier for Player.ApplyMovementSpeedAt (0x141a70760); 1 when none runs.
    public static double MovementSpeedMultiplier(Player player) =>
        ShieldChargeRuns.TryGetValue(player, out var run) && run.Traveling && PlayerActionCurrent(player, run.ActionToken)
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
        double seconds = ShieldWeapon(player, skillId) is { } shieldWeapon
            ? SupportedOffHandAttackSeconds(player, skillId, shieldWeapon)
            : SupportedAttackSeconds(player, skillId);
        if (noCharge && SupportedStats(player, skillId).GetValueOrDefault(Stat.ShieldChargeAttackTimePlus30PctIfNoCharge) != 0)
            seconds *= 1.3;
        return seconds;
    }

    static void SyncShieldChargeMovementStat(Player player)
    {
        RecomputePoolsAndSpeed(player);
        // Publish into Area.Stats immediately so the 0x1b3 movement-speed update ships in the same frame as Shield Charge's
        // 0x1ab start/end (and RelayWalkForStats updates player.Walk.Speed before the frame steps).
        if (player.Connection.LockstepClient && player.Frames.Running && player.Area.Stats.HasWritten(player.Id))
            player.Area.Stats.Set(player.Id, Stat.MovementVelocityPlusPct,
                player.Pools.MovementVelocityPct + player.Rampage.Current.MovementPercent);
    }

    static void EndShieldChargeMovement(Player player, ShieldChargeRun run, double? arrivedAt = null)
    {
        if (!run.Traveling && run.Percent == 0) return;
        run.Traveling = false;
        run.Percent = 0;
        run.AttackSpeedMultiplier = 1;
        if (arrivedAt is double at && run.EndAttackSeconds > 0)
            player.NextAttackAt = Math.Min(player.NextAttackAt, at + run.EndAttackSeconds);
        SyncShieldChargeMovementStat(player);
    }

    /// <summary>Whether Shield Charge is still in its un-cancellable travel phase (+0x3e5 != 0, 0x141a6fd50) at
    /// <paramref name="at"/>. Clears the charge movement bonus on arrival or interruption.</summary>
    public static bool ShieldChargeTraveling(Player player, double at)
    {
        if (!ShieldChargeRuns.TryGetValue(player, out var run) || !run.Traveling) return false;
        if (!PlayerActionCurrent(player, run.ActionToken) || player.Closed || player.PlayerLife == 0)
        {
            EndShieldChargeMovement(player, run);
            return false;
        }
        if (at < player.Walk.ArrivesAt - 1e-9) return true;
        run.Arrived = true;
        run.ArrivedAtCell = player.Walk.Position(player.Walk.ArrivesAt);
        EndShieldChargeMovement(player, run, player.Walk.ArrivesAt);
        return false;
    }

    public static void EndShieldChargeIfInterrupted(Player player)
    {
        if (ShieldChargeRuns.TryGetValue(player, out var run) && (run.Traveling || run.Percent != 0))
            EndShieldChargeMovement(player, run);
    }

    // At paid release: the bonus on, the pools and walk speed with it, and the walk to the aimed cell, no further
    // than the charge's maximum distance. Returns the run's token, which only the latest charge's end may clear.
    public static int BeginShieldCharge(Player player, ushort skillId, int percent, int x, int y, double at) =>
        BeginShieldCharge(player, skillId, percent, x, y, at, player.ActionCount, out _);

    public static int BeginShieldCharge(Player player, ushort skillId, int percent, int x, int y, double at, int actionToken, out bool charged)
    {
        var run = ShieldChargeRuns.GetOrCreateValue(player);
        int max = ShieldChargeMaxDistance(player, skillId);
        int extra = SupportedStats(player, skillId).GetValueOrDefault(Stat.ShieldChargeExtraDistance);
        double attackMult = ShieldChargeAttackSpeedMultiplier(player, skillId);
        double startAt = player.Connection.LockstepClient && player.Connection.LockstepFrame is { } frame ? frame.Start : at;
        run.Start = player.Walk.Position(startAt);
        run.MaxDistance = Math.Max(1, max);
        run.ActionToken = actionToken;
        run.Arrived = false;
        int token = ++run.Token;
        player.Walk.Charge(x, y, max, startAt, extra, ShieldChargeMinDistance, out charged);
        if (!charged)
        {
            bool hadBonus = run.Traveling || run.Percent != 0;
            run.Traveling = false;
            run.Percent = 0;
            run.AttackSpeedMultiplier = 1;
            double endSeconds = ShieldChargeEndAttackSeconds(player, skillId, noCharge: true);
            run.EndAttackSeconds = endSeconds;
            if (hadBonus) SyncShieldChargeMovementStat(player);
            player.NextAttackAt = AttackTiming.After(startAt, player.NextAttackAt, endSeconds);
            if (player.Connection.LockstepClient)
            {
                double frameEnd = player.Connection.LockstepFrame?.End ?? startAt;
                string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
                double cutIn = endSeconds * Animations.AttackCutIn(ao, AnimationWeaponProfileFor(player), "bash1");
                player.LockstepBusyUntil = Math.Max(player.LockstepBusyUntil, frameEnd + cutIn + player.LockstepActionTail);
            }
            return token;
        }
        run.Percent = percent;
        run.Traveling = true;
        run.AttackSpeedMultiplier = attackMult;
        run.EndAttackSeconds = ShieldChargeEndAttackSeconds(player, skillId, noCharge: false);
        SyncShieldChargeMovementStat(player);
        // Re-lay the newly started charge walk with the updated off-hand attack speed multiplier (on Lockstep,
        // RelayWalkForStats will also apply the +90% movement speed before this frame ticks).
        player.ApplyMovementSpeedAt(startAt);
        player.NextAttackAt = Math.Max(player.NextAttackAt, player.Walk.ArrivesAt + run.EndAttackSeconds);
        return token;
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
        return new SkillCatalog.Unarmed(attackMs > 0 ? attackMs : WeaponFor(player).AttackMs, min, Math.Max(min, max), WeaponFor(player).Range);
    }

    // The client's charge hits (Frida 2026-11-01, scripts/re/frida/trace-charge-hits.js): a path hit at its position as it
    // starts and then about every 100 ms (every ~8 cells at charge speed), each within 8 cells, and a final hit within
    // 16 cells where it arrives. The server lands them on its own walk at the same cadence; each monster takes one path hit
    // per charge (neighbouring path areas overlap), and the final hit reaches every monster in its area.
    const double ShieldChargePathSeconds = 0.1;
    const int ShieldChargePathRadius = 8, ShieldChargeEndRadius = 16;

    // After the use's echo: the faster movement speed to the client, the path hits while the server's walk runs, the final
    // hit and the speed back where it arrives (a later charge or interruption takes over the run, its hits and its end).
    public static Task RunShieldCharge(Player player,
        int token, double arrivesAt, ushort skillId, ushort sequence, int actionToken, CooldownUseTicket? cooldownUse, bool cryExerted) =>
        RunShieldCharge(player, token, charged: true, skillId, sequence, actionToken, cooldownUse, cryExerted);

    public static async Task RunShieldCharge(Player player,
        int token, bool charged, ushort skillId, ushort sequence, int actionToken, CooldownUseTicket? cooldownUse, bool cryExerted)
    {
        bool Current() => ShieldChargeRuns.TryGetValue(player, out var r) && r.Token == token
            && (r.Arrived || PlayerActionCurrent(player, actionToken)) && !player.Closed && player.PlayerLife > 0;
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
        var (start, maxDistance) = ShieldChargeRuns.TryGetValue(player, out var begun) ? (begun.Start, begun.MaxDistance) : default;
        int maximumMore = Stat.ShieldChargeDamagePlusPctMaximum is var moreKey
            ? SupportedStats(player, skillId).GetValueOrDefault(moreKey) : 0;
        double More((int X, int Y) at) => maxDistance > 0 ? 1 + maximumMore / 100.0 * Math.Min(1, Dist(start, at) / maxDistance) : 1;
        if (ShieldWeapon(player, skillId) is { } shieldWeapon)
            Console.WriteLine($"[gateway] {player.Connection.Remote} Shield Charge damage: shield base {shieldWeapon.MinDamage}-{shieldWeapon.MaxDamage}, " +
                $"up to {maximumMore}% more at {maxDistance:0} cells{(charged ? "" : " (no-charge)")}");
        Task Hit((int X, int Y) at, int radius, ISet<uint>? once, int? hitAction) => gem.Name is null ? Task.CompletedTask
            : AreaSkillHit(player, skillId, gem, at, at, immediate: true, sequence: sequence,
                actionToken: hitAction, cooldownUse: cooldownUse, damageMore: More(at), radiusOverride: radius, once: once, cryExerted: cryExerted);
        if (!charged)
        {
            if (Current()) await Hit(player.Walk.Position(AreaTime(player)), ShieldChargeEndRadius, null, actionToken);
            return;
        }
        await SendPlayerStatSnapshot(player, () => PlayerStatsOffFrames(player),
            $"0x01b3 player stats (Shield Charge starts: movement_velocity_+% {player.Pools.MovementVelocityPct}, " +
            $"+{ShieldChargeMovementPct(player)} while charging)");
        var pathHit = new HashSet<uint>();
        // The step pass may end the run early on a body or a wall, and a mid-charge speed change updates ArrivesAt live.
        for (double tick = AreaTime(player); tick < player.Walk.ArrivesAt; tick += ShieldChargePathSeconds)
        {
            double wait = tick - AreaTime(player);
            if (wait > 0) await AreaDelay(player, wait);
            await WhilePaused(player);
            if (!Current() || ShieldChargeRuns.TryGetValue(player, out var check) && check.Arrived) break;
            if (AreaTime(player) >= player.Walk.ArrivesAt) break;
            await Hit(player.Walk.Position(AreaTime(player)), ShieldChargePathRadius, pathHit, actionToken);
        }
        while (Current() && !(ShieldChargeRuns.TryGetValue(player, out var waitRun) && waitRun.Arrived))
        {
            double rest = player.Walk.ArrivesAt - AreaTime(player);
            if (rest <= 1e-6) break;
            await AreaDelay(player, rest);
            await WhilePaused(player);
        }
        if (!Current() || !ShieldChargeRuns.TryGetValue(player, out var run)) return;
        // On Lockstep a Move can immediately cancel charge_*_end on the arrival frame, after the client already delivered
        // the arrival hit (0x141a70910); when run.Arrived is true, still land the arrival hit at the arrival cell.
        var endAt = run.Arrived ? run.ArrivedAtCell : player.Walk.Position(AreaTime(player));
        await Hit(endAt, ShieldChargeEndRadius, null, run.Arrived ? null : actionToken);
        EndShieldChargeMovement(player, run, player.Walk.ArrivesAt);
        await SendPlayerStatSnapshot(player, () => PlayerStatsOffFrames(player),
            $"0x01b3 player stats (Shield Charge ends: movement_velocity_+% {player.Pools.MovementVelocityPct})");
    }
}
