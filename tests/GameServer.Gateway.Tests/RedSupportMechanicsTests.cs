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
        // Maim: a hit's chance maims for 4 s, and a maimed monster takes 10% more physical damage.
        var monster = new SpawnedMonster("maimed", new(105, 100, 0), 0, 0, 1000);
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        Assert.Equal(100, Game.MonsterTakesMitigated(monster, physical, spell: false, now: 1));
        Game.HitDebuffsOnHitLocked(monster,
            Stats(("maim_on_hit_%", 100), ("support_maimed_enemies_physical_damage_taken_+%", 10)),
            spell: false, critical: false, now: 10);
        Assert.Equal(14, monster.MaimedUntil);
        Assert.Equal(110, Game.MonsterTakesMitigated(monster, physical, spell: false, now: 11));
        Assert.Equal(100, Game.MonsterTakesMitigated(monster, physical, spell: false, now: 14));

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
