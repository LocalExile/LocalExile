using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Shield Charge's movement (11-skills.md "Shield Charge"). The client moves the charge at the player's own movement
    // speed: 0x141e827a0 reads movement_velocity_+% from the player's Stats, not the skill's. The game adds the gem's
    // base_movement_velocity_+% through the invisible buff shield_charge_movement_speed_while_charging (BuffDefinitions
    // 1352) for as long as the charge runs, and network stats run no calculators, so the server puts that bonus into the
    // movement_velocity_+% it sends (the pools) from the use until its own walk arrives, and walks its estimate to the aimed
    // cell at that speed instead of the generic movement skill's teleport. Without it the charge ran at walk speed.
    sealed class ShieldChargeRun { public int Percent, Token; public (int X, int Y) Start; public double MaxDistance; }
    static readonly Store<Player, ShieldChargeRun> ShieldChargeRuns = new();

    // The running charge's movement bonus, for the pools (PoolsFor); 0 when none runs.
    public static int ShieldChargeMovementPct(Player player) => ShieldChargeRuns.TryGetValue(player, out var run) ? run.Percent : 0;

    // A movement skill that needs a shield and carries its own movement bonus: it walks there faster, it does not blink.
    public static int ShieldChargeBonusPct(Player player, ushort skillId, int effect) =>
        SkillsFor(player).IsMovement(effect) && SkillsFor(player).RequiresShield(effect)
            && Stat.BaseMovementVelocityPlusPct is var key ? SupportedSkill(player, skillId).Stats.GetValueOrDefault(key) : 0;

    // The client's setup 0x141a6e4f0 stops a charge aimed further away at 75 cells × (1 + shield_charge_range_+% / 100)
    // from where it starts (integer, (100 + range) * 75 / 100), along the aim: the "Maximum Charge Distance" of the gem's
    // damage line. shortcut: the setup also adds shield_charge_extra_distance past the aim and uses 14 cells while an
    // action counter (+0x2d0 below +0x2d4) is short; neither is modelled.
    const int ShieldChargeBaseDistance = 75;

    public static int ShieldChargeMaxDistance(Player player, ushort skillId) =>
        (100 + (Stat.ShieldChargeRangePlusPct is var k ? SupportedStats(player, skillId).GetValueOrDefault(k) : 0))
            * ShieldChargeBaseDistance / 100;

    // In the use's StartAction: the bonus on, the pools and walk speed with it, and the walk to the aimed cell, no further
    // than the charge's maximum distance. Returns the run's token, which only the latest charge's end may clear.
    public static int BeginShieldCharge(Player player, ushort skillId, int percent, int x, int y, double at)
    {
        var run = ShieldChargeRuns.GetOrCreateValue(player);
        int max = ShieldChargeMaxDistance(player, skillId);
        run.Percent = percent;
        run.Start = player.Walk.Position(at);
        run.MaxDistance = Math.Max(1, max);
        int token = ++run.Token;
        RecomputePoolsAndSpeed(player);
        player.Walk.Charge(x, y, max, at);
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
    // hit and the speed back where it arrives (a later charge takes over the run, its hits and its end).
    public static async Task RunShieldCharge(Player player,
        int token, double arrivesAt, ushort skillId, ushort sequence, int actionToken, CooldownUseTicket? cooldownUse)
    {
        await SendPlayerStatSnapshot(player, () => PlayerStatsOffFrames(player),
            $"0x01b3 player stats (Shield Charge starts: movement_velocity_+% {player.Pools.MovementVelocityPct}, " +
            $"+{ShieldChargeMovementPct(player)} while charging, until t={arrivesAt:0.000})");
        bool Current() => ShieldChargeRuns.TryGetValue(player, out var r) && r.Token == token && !player.Closed;
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
        var pathHit = new HashSet<uint>();
        // "{0}% more Damage with Hits at Maximum Charge Distance" (shield_charge_damage_+%_maximum, 100): each hit's share
        // of it is the distance charged so far over the maximum. shortcut: a linear ramp, not read from the client.
        var (start, maxDistance) = ShieldChargeRuns.TryGetValue(player, out var begun) ? (begun.Start, begun.MaxDistance) : default;
        int maximumMore = Stat.ShieldChargeDamagePlusPctMaximum is var moreKey
            ? SupportedStats(player, skillId).GetValueOrDefault(moreKey) : 0;
        double More((int X, int Y) at) => maxDistance > 0 ? 1 + maximumMore / 100.0 * Math.Min(1, Dist(start, at) / maxDistance) : 1;
        if (ShieldWeapon(player, skillId) is { } shieldWeapon)
            Console.WriteLine($"[gateway] {player.Connection.Remote} Shield Charge damage: shield base {shieldWeapon.MinDamage}-{shieldWeapon.MaxDamage}, " +
                $"up to {maximumMore}% more at {maxDistance:0} cells");
        Task Hit((int X, int Y) at, int radius, ISet<uint>? once) => gem.Name is null ? Task.CompletedTask
            : AreaSkillHit(player, skillId, gem, at, at, immediate: true, sequence: sequence,
                actionToken: actionToken, cooldownUse: cooldownUse, damageMore: More(at), radiusOverride: radius, once: once);
        // The step pass may end the run early, on a body or a wall.
        double Arrival() => Math.Min(arrivesAt, player.Walk.ArrivesAt);
        for (double tick = AreaTime(player); tick < Arrival(); tick += ShieldChargePathSeconds)
        {
            double wait = tick - AreaTime(player);
            if (wait > 0) await AreaDelay(player, wait);
            await WhilePaused(player);
            if (!Current()) return;
            await Hit(player.Walk.Position(AreaTime(player)), ShieldChargePathRadius, pathHit);
        }
        double rest = Arrival() - AreaTime(player);
        if (rest > 0) await AreaDelay(player, rest);
        if (!ShieldChargeRuns.TryGetValue(player, out var run) || run.Token != token || player.Closed) return;
        await Hit(player.Walk.Position(AreaTime(player)), ShieldChargeEndRadius, null);
        run.Percent = 0;
        RecomputePoolsAndSpeed(player);
        await SendPlayerStatSnapshot(player, () => PlayerStatsOffFrames(player),
            $"0x01b3 player stats (Shield Charge ends: movement_velocity_+% {player.Pools.MovementVelocityPct})");
    }
}
