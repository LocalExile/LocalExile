using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// Red/blue supports whose stats had no consumer: Maim, Bloodlust, Power Charge on Critical and Volatility.
public sealed class RedSupportMechanicsTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] stats) =>
        name => stats.Where(s => s.Name == name).Sum(s => s.Value);

    static Dictionary<uint, int> With(params (string Name, int Value)[] stats) =>
        stats.ToDictionary(s => Program.Stats.Key(s.Name) ?? throw new InvalidOperationException(s.Name), s => s.Value);

    [Fact]
    public void Maim_bloodlust_power_charges_and_volatility_apply()
    {
        static MonsterModSet Target(params (string Name, int Value)[] stats) =>
            new([], [], new(stats.ToDictionary(s => s.Name, s => s.Value)));

        // A plain hit Maims for 4 s and changes movement only.
        var plain = new SpawnedMonster("plain", new(105, 100, 100), 0, 0, 1000);
        Game.HitDebuffsOnHitLocked(plain, Stats(("maim_on_hit_%", 100)), spell: false, critical: false, now: 10,
            new Random(1));
        Assert.Equal(14, plain.MaimedUntil);
        Assert.Equal(0, Game.MaimedPhysicalTaken(plain, 11));
        Assert.Equal((.7, -30), Game.MaimedMovementFactor(plain, 11));

        // The Maim Support's own physical-taken property has its own deadline. A later plain Maim extends movement, not it.
        var monster = new SpawnedMonster("maimed", new(105, 100, 100), 0, 0, 1000);
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        Assert.Equal(100, Game.MonsterTakesMitigated(monster, physical, spell: false, now: 1));
        Game.HitDebuffsOnHitLocked(monster,
            Stats(("maim_on_hit_%", 100), ("support_maimed_enemies_physical_damage_taken_+%", 10)),
            spell: false, critical: false, now: 10, new Random(1));
        Assert.Equal(14, monster.MaimedUntil);
        Assert.Equal(14, monster.MaimedPhysicalTakenUntil);
        Assert.Equal(110, Game.MonsterTakesMitigated(monster, physical, spell: false, now: 11));
        Game.HitDebuffsOnHitLocked(monster, Stats(("maim_on_hit_%", 100)), spell: false, critical: false, now: 11,
            new Random(2));
        Assert.Equal(15, monster.MaimedUntil);
        Assert.Equal(14, monster.MaimedPhysicalTakenUntil);
        Assert.Equal(110, Game.MonsterTakesMitigated(monster, physical, spell: false, now: 13));
        Assert.Equal(100, Game.MonsterTakesMitigated(monster, physical, spell: false, now: 14));

        // A refreshed support keeps one value, does not stack, and owns its new deadline.
        Game.HitDebuffsOnHitLocked(monster,
            Stats(("maim_on_hit_%", 100), ("support_maimed_enemies_physical_damage_taken_+%", 10)),
            spell: false, critical: false, now: 12, new Random(3));
        Assert.Equal((10, 16.0), (Game.MaimedPhysicalTaken(monster, 15), monster.MaimedPhysicalTakenUntil));
        Assert.Equal(110, Game.MonsterTakesMitigated(monster, physical, spell: false, now: 15));
        Assert.Equal(100, Game.MonsterTakesMitigated(monster, physical, spell: false, now: 16));

        // Maim folds into the existing movement bucket. A +50% monster's walk is 120%, not 150% × 70% = 105%.
        var movement = new SpawnedMonster("fast", new(105, 100, 150), 0, 0, 1000)
        {
            Mods = Target(("base_movement_velocity_+%", 50)), MaimedUntil = 14, SpeedRoll = 7,
        };
        var maimed = Game.MaimedMovementFactor(movement, 11);
        Assert.Equal(.8, maimed.Factor, 9);
        Assert.Equal(20, maimed.Pct);
        Assert.Equal(120, 150 * maimed.Factor, 9);
        movement.MaimedUntil = double.NegativeInfinity;
        Assert.Equal((1, 50), Game.MaimedMovementFactor(movement, 11));
        Assert.Equal(7, movement.SpeedRoll);

        // A real variety's MovementSpeed floor is absolute: -90% halves a 1-cell/s base to 0.5, but
        // MonsterModEffects.MovementSpeed keeps both before and after at 1. The speed roll still multiplies both.
        var floored = new SpawnedMonster("floored", new(105, 100, 1.5), 0, 0, 1000)
        {
            Mods = Target(("base_movement_velocity_+%", -90)), MaimedUntil = 14, SpeedRoll = 50,
            Variety = new MonsterCatalog.Variety(0, "Floored", "Floored", "Floored", null, null, MovementSpeed: 1),
        };
        var flooredMaim = Game.MaimedMovementFactor(floored, 11);
        Assert.Equal(1, flooredMaim.Factor, 9);
        Assert.Equal(-120, flooredMaim.Pct);

        // Target immunity and avoidance stop the application; dead, lifeless and friendly targets do too.
        var immune = new SpawnedMonster("immune", new(105, 100, 100), 0, 0, 1000)
            { Mods = Target(("immune_to_maim", 1)) };
        var avoid = new SpawnedMonster("avoid", new(105, 100, 100), 0, 0, 1000)
            { Mods = Target(("avoid_maim_%_chance", 100)) };
        var dead = new SpawnedMonster("dead", new(105, 100, 100), 0, 0, 1000) { Dead = true };
        var lifeless = new SpawnedMonster("lifeless", new(105, 100, 100), 0, 0, 1000) { Life = 0 };
        var friendly = new SpawnedMonster("friendly", new(105, 100, 100), 0, 0, 1000) { Hostile = false };
        foreach (var target in new[] { immune, avoid, dead, lifeless, friendly })
            Game.HitDebuffsOnHitLocked(target, Stats(("maim_on_hit_%", 100)), spell: false, critical: false, now: 10,
                new Random(4));
        Assert.All(new[] { immune, avoid, dead, lifeless, friendly },
            target => Assert.Equal(double.NegativeInfinity, target.MaimedUntil));

        // The support benefit cannot outlive Maim when enemy-debuff cleanup ends movement first.
        monster.MaimedUntil = double.NegativeInfinity;
        Assert.Equal(0, Game.MaimedPhysicalTaken(monster, 13));

        // The actual ailment tick publishes the folded movement bucket and updates the native walk without
        // compounding, then restores the speed roll at expiry. Base 50, +50%, and a +50% object roll walk at 112.5.
        var conn = new ClientState(new(1, "maim review", 300, 300, 100, 100, null, []), 1, 1);
        const uint id = 0x21a2;
        double baseSpeed = 50, roll = 1.5;
        var ticked = new SpawnedMonster("ticked", new(105, 100, baseSpeed * 1.5 * roll), 0, 0, 1000)
        {
            Mods = Target(("base_movement_velocity_+%", 50)), SpeedRoll = 50,
            Variety = new MonsterCatalog.Variety(0, "Ticked", "Ticked", "Ticked", null, null, MovementSpeed: 50),
        };
        conn.Monsters[id] = ticked;
        Game.HitDebuffsOnHitLocked(ticked, Stats(("maim_on_hit_%", 100)), spell: false, critical: false, now: 10,
            new Random(1));
        bool MovementPacket(IEnumerable<(byte[] Packet, string Log)> packets, int pct, uint targetId = id) =>
            packets.Any(p => p.Packet.SequenceEqual(InstanceEntities.StatsUpdate(targetId,
                [(StatKeys.MovementVelocityPct, pct)])));
        var firstTick = Game.MonsterAilmentChangesLocked(conn, now: 11);
        Assert.Equal(baseSpeed * 1.2 * roll, ticked.Walk.Speed, 9);
        Assert.True(MovementPacket(firstTick, 20));
        var repeatedTick = Game.MonsterAilmentChangesLocked(conn, now: 12);
        Assert.Equal(baseSpeed * 1.2 * roll, ticked.Walk.Speed, 9);
        Assert.False(MovementPacket(repeatedTick, 20));
        var expiryTick = Game.MonsterAilmentChangesLocked(conn, now: 14);
        Assert.Equal(baseSpeed * 1.5 * roll, ticked.Walk.Speed, 9);
        Assert.True(MovementPacket(expiryTick, 50));

        // A chill composes with Maim through action speed while Maim alone publishes only the movement bucket.
        ticked.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill, 4, now: 15, Stats(),
            name => ticked.Effects[name], stated: 20);
        Game.MonsterAilmentChangesLocked(conn, now: 16);
        int chilledAction = ticked.Ailments.SentActionSpeed;
        Assert.Equal(-20, chilledAction);
        Game.HitDebuffsOnHitLocked(ticked, Stats(("maim_on_hit_%", 100)), spell: false, critical: false, now: 16,
            new Random(1));
        var composedTick = Game.MonsterAilmentChangesLocked(conn, now: 17);
        Assert.Equal(baseSpeed * 1.2 * .8 * roll, ticked.Walk.Speed, 9);
        Assert.Equal(chilledAction, ticked.Ailments.SentActionSpeed);
        Assert.True(MovementPacket(composedTick, 20));

        // By expiry the +20% bucket can already equal the shown maimed value. No movement packet is needed, but the
        // semantic state still has to leave Maim so a later Maim publishes again.
        var sameValue = new SpawnedMonster("same value", new(105, 100, 90), 0, 0, 1000)
        {
            Mods = Target(("base_movement_velocity_+%", 20)), MaimedUntil = double.NegativeInfinity,
            SpeedRoll = 50, MaimedMovementShown = true, MaimedMovementShownPct = 20,
            Variety = new MonsterCatalog.Variety(0, "Same", "Same", "Same", null, null, MovementSpeed: 50),
        };
        conn.Monsters[id + 1] = sameValue;
        var sameValueTick = Game.MonsterAilmentChangesLocked(conn, now: 18);
        Assert.False(MovementPacket(sameValueTick, 20, id + 1));
        Assert.False(sameValue.MaimedMovementShown);
        Assert.Equal(0, sameValue.MaimedMovementShownPct);

        // Bloodlust: more melee physical damage against a bleeding enemy only.
        var bloodlust = With(("support_bloodlust_melee_physical_damage_+%_final_vs_bleeding_enemies", 39));
        Assert.Equal(1.39, Game.ConditionalSupportMore(bloodlust, attack: true, chilled: false, lowLife: false,
            fullLife: false, lifetap: false, bleeding: true), 9);
        Assert.Equal(1, Game.ConditionalSupportMore(bloodlust, attack: true, chilled: false, lowLife: false,
            fullLife: false, lifetap: false), 9);

        // Power Charge on Critical: 4% more damage per power charge.
        var power = With(("support_power_charge_on_crit_damage_+%_final_per_power_charge", 4));
        Assert.Equal(1.12, Game.ConditionalSupportMore(power, attack: false, chilled: false, lowLife: false,
            fullLife: false, lifetap: false, powerCharges: 3), 9);

        // Volatility: less minimum, more maximum attack damage.
        var volatileHit = Volatility.Apply(DamagePacket.Of(DamageType.Physical, 100, 200),
            Stats(("minimum_attack_damage_+%_final_from_volatility_support", -50),
                ("maximum_attack_damage_+%_final_from_volatility_support", 100)));
        Assert.Equal((50, 400), (volatileHit[DamageType.Physical].Min, volatileHit[DamageType.Physical].Max));

        // Combustion: the ignite's fire resistance reduction rides on the ignite settings.
        Assert.Equal(-10, MonsterIgnites.Settings.From([], "main_hand",
            Stats(("ignites_apply_fire_resistance_+", -10))).FireExposure);

        // Iron Grip: Strength's bonus joins projectile attack physical damage only.
        Assert.Equal(20, DamageStats.StrengthDamageBonus(Stats(("strength", 100))), 9);
        var strength = Stats(("strength", 100), ("keystone_strong_bowman", 1));
        var withoutGrip = Stats(("strength", 100));
        Assert.Equal(20, DamageStats.AttackIncrease(strength, DamageType.Physical, melee: false, projectile: true)
            - DamageStats.AttackIncrease(withoutGrip, DamageType.Physical, melee: false, projectile: true), 9);
        Assert.Equal(0, DamageStats.AttackIncrease(strength, DamageType.Physical, melee: true, projectile: true)
            - DamageStats.AttackIncrease(withoutGrip, DamageType.Physical, melee: true, projectile: true), 9);

        // Coursing Current: a chill also inflicts the equivalent shock on the same hit.
        var ailments = new MonsterElementalAilments();
        var coursing = ailments.Inflict(DamagePacket.Of(DamageType.Cold, 50, 50), critical: false, threshold: 100, now: 0,
            Stats(("inflict_equivalent_shock_on_inflicting_chill_with_hits", 1)), Stats(), new Random(1));
        Assert.Contains(coursing, e => e.Kind == MonsterElementalAilments.Kind.Chill);
        Assert.Contains(coursing, e => e.Kind == MonsterElementalAilments.Kind.Shock);
    }
}
