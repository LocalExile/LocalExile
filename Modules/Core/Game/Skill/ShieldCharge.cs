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
    static readonly ConditionalWeakTable<ClientState, ShieldChargeRun> ShieldChargeRuns = new();

    // The running charge's movement bonus, for the pools (PoolsFor); 0 when none runs.
    public static int ShieldChargeMovementPct(ClientState conn) => ShieldChargeRuns.TryGetValue(conn, out var run) ? run.Percent : 0;

    // A movement skill that needs a shield and carries its own movement bonus: it walks there faster, it does not blink.
    public static int ShieldChargeBonusPct(ClientState conn, ushort skillId, int effect) =>
        SkillsFor(conn).IsMovement(effect) && SkillsFor(conn).RequiresShield(effect)
            && Stats.Key("base_movement_velocity_+%") is uint key ? SupportedSkill(conn, skillId).Stats.GetValueOrDefault(key) : 0;

    // The client's setup 0x141a6e4f0 stops a charge aimed further away at 75 cells × (1 + shield_charge_range_+% / 100)
    // from where it starts (integer, (100 + range) * 75 / 100), along the aim: the "Maximum Charge Distance" of the gem's
    // damage line. shortcut: the setup also adds shield_charge_extra_distance past the aim and uses 14 cells while an
    // action counter (+0x2d0 below +0x2d4) is short; neither is modelled.
    const int ShieldChargeBaseDistance = 75;

    public static int ShieldChargeMaxDistance(ClientState conn, ushort skillId) =>
        (100 + (Stats.Key("shield_charge_range_+%") is uint k ? SupportedStats(conn, skillId).GetValueOrDefault(k) : 0))
            * ShieldChargeBaseDistance / 100;

    // After paid admission: the bonus on, the pools and walk speed with it, and the walk to the aimed cell, no further
    // than the charge's maximum distance. Returns the run's token, which only the latest charge's end may clear.
    public static int BeginShieldCharge(ClientState conn, ushort skillId, int percent, int x, int y, double at)
    {
        var run = ShieldChargeRuns.GetOrCreateValue(conn);
        int max = ShieldChargeMaxDistance(conn, skillId);
        lock (conn.Monsters)
        {
            run.Percent = percent;
            run.Start = conn.Walk.Position(at);
            run.MaxDistance = Math.Max(1, max);
            int token = ++run.Token;
            RecomputePoolsAndSpeed(conn);
            conn.Walk.Charge(x, y, max, at);
            return token;
        }
    }

    // A shield skill's attack swings the shield (Shield Charge, Shield Crush), which has no weapon damage: the gem's own
    // off_hand_local_minimum/maximum_added_physical_damage plus its added damage per 15 of the shield's armour and evasion
    // rating (the item's own, local values: "per 15 Armour or Evasion Rating on Shield"), at the gem's AttackTime. Null
    // for other skills and without a shield, whose attacks use the main hand's weapon.
    public static SkillCatalog.Unarmed? ShieldWeapon(ClientState conn, ushort skillId)
    {
        var gem = SupportedSkill(conn, skillId);
        if (!SkillsFor(conn).IsAttack(gem.Effect) || !SkillsFor(conn).RequiresShield(gem.Effect)
            || ActiveOffHand(conn)?.Type?.Class != "Shield") return null;
        int Own(string name) => Stats.Key(name) is uint k ? gem.Stats.GetValueOrDefault(k) : 0;
        var shield = EquippedArmourDefences(conn).FirstOrDefault(d => d.Row == PlayerInventories.Offhand1).Defence;
        int per15 = (int)((shield.Armour + shield.Evasion) / 15);
        int min = Own("off_hand_local_minimum_added_physical_damage")
            + per15 * Own("off_hand_minimum_added_physical_damage_per_15_shield_armour_and_evasion_rating");
        int max = Own("off_hand_local_maximum_added_physical_damage")
            + per15 * Own("off_hand_maximum_added_physical_damage_per_15_shield_armour_and_evasion_rating");
        int attackMs = SkillsFor(conn).AttackTimeMs(gem.Effect, gem.Level);
        return new SkillCatalog.Unarmed(attackMs > 0 ? attackMs : WeaponFor(conn).AttackMs, min, Math.Max(min, max), WeaponFor(conn).Range);
    }

    // The client's charge hits (Frida 2026-11-01, scripts/re/frida/trace-charge-hits.js): a path hit at its position as it
    // starts and then about every 100 ms (every ~8 cells at charge speed), each within 8 cells, and a final hit within
    // 16 cells where it arrives. The server lands them on its own walk at the same cadence; each monster takes one path hit
    // per charge (neighbouring path areas overlap), and the final hit reaches every monster in its area.
    const double ShieldChargePathSeconds = 0.1;
    const int ShieldChargePathRadius = 8, ShieldChargeEndRadius = 16;

    // After the use's echo: the faster movement speed to the client, the path hits while the server's walk runs, the final
    // hit and the speed back where it arrives (a later charge takes over the run, its hits and its end).
    public static async Task RunShieldCharge(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        int token, double arrivesAt, ushort skillId, ushort sequence, int actionToken, CooldownUseTicket? cooldownUse)
    {
        await SendPlayerStatSnapshot(remote, stream, ciphers, conn, () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)),
            $"0x01b3 player stats (Shield Charge starts: movement_velocity_+% {conn.Pools.MovementVelocityPct}, " +
            $"+{ShieldChargeMovementPct(conn)} while charging, until t={arrivesAt:0.000})");
        bool Current() => ShieldChargeRuns.TryGetValue(conn, out var r) && r.Token == token && !conn.Closed;
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        var pathHit = new HashSet<uint>();
        // "{0}% more Damage with Hits at Maximum Charge Distance" (shield_charge_damage_+%_maximum, 100): each hit's share
        // of it is the distance charged so far over the maximum. shortcut: a linear ramp, not read from the client.
        var (start, maxDistance) = ShieldChargeRuns.TryGetValue(conn, out var begun) ? (begun.Start, begun.MaxDistance) : default;
        int maximumMore = Stats.Key("shield_charge_damage_+%_maximum") is uint moreKey
            ? SupportedStats(conn, skillId).GetValueOrDefault(moreKey) : 0;
        double More((int X, int Y) at) => maxDistance > 0 ? 1 + maximumMore / 100.0 * Math.Min(1, Dist(start, at) / maxDistance) : 1;
        if (ShieldWeapon(conn, skillId) is { } shieldWeapon)
            Console.WriteLine($"[gateway] {remote} Shield Charge damage: shield base {shieldWeapon.MinDamage}-{shieldWeapon.MaxDamage}, " +
                $"up to {maximumMore}% more at {maxDistance:0} cells");
        Task Hit((int X, int Y) at, int radius, ISet<uint>? once) => gem.Name is null ? Task.CompletedTask
            : AreaSkillHit(remote, stream, ciphers, conn, skillId, gem, at, at, immediate: true, sequence: sequence,
                actionToken: actionToken, cooldownUse: cooldownUse, damageMore: More(at), radiusOverride: radius, once: once);
        // The step pass may end the run early, on a body or a wall.
        double Arrival() => Math.Min(arrivesAt, conn.Walk.ArrivesAt);
        for (double tick = conn.Clock.Elapsed.TotalSeconds; tick < Arrival(); tick += ShieldChargePathSeconds)
        {
            double wait = tick - conn.Clock.Elapsed.TotalSeconds;
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
            await WhilePaused(conn);
            if (!Current()) return;
            await Hit(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), ShieldChargePathRadius, pathHit);
        }
        double rest = Arrival() - conn.Clock.Elapsed.TotalSeconds;
        if (rest > 0) await Task.Delay(TimeSpan.FromSeconds(rest));
        if (!ShieldChargeRuns.TryGetValue(conn, out var run) || run.Token != token || conn.Closed) return;
        await Hit(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), ShieldChargeEndRadius, null);
        lock (conn.Monsters)
        {
            run.Percent = 0;
            RecomputePoolsAndSpeed(conn);
        }
        await SendPlayerStatSnapshot(remote, stream, ciphers, conn, () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)),
            $"0x01b3 player stats (Shield Charge ends: movement_velocity_+% {conn.Pools.MovementVelocityPct})");
    }
}
