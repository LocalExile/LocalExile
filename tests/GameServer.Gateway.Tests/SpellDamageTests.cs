using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class SpellDamageTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] stats) => name => stats.FirstOrDefault(s => s.Name == name).Value;

    [Fact]
    public void Fireball_level_1_without_increases_is_its_base()
    {
        Assert.Equal((9, 14), SpellDamage.Hit(Stats(("spell_minimum_base_fire_damage", 9), ("spell_maximum_base_fire_damage", 14))));
    }

    [Fact]
    public void Actual_Fireball_uses_three_hundred_percent_added_effectiveness_without_rescaling_its_base_damage()
    {
        var skills = new SkillCatalog("data");
        int effect = skills.EffectRow("Fireball");
        Assert.Equal(30000, skills.DamageEffectiveness(effect, 1)); // raw GESSPL delta 20000 means 1+2.
        Assert.Equal(10000, skills.AttackBaseMultiplier(effect, 1)); // raw zero is identity.
        var hit = SpellDamage.Packet(skills.HitDamage(effect, 1),
            Stats(("spell_minimum_added_fire_damage", 2), ("spell_maximum_added_fire_damage", 4)),
            skills.DamageEffectiveness(effect, 1) / 10000.0);
        Assert.Equal(new DamageRange(15, 26), hit[DamageType.Fire]); // 9..14 base + (2..4)*3.
    }

    [Fact]
    public void An_explicit_normalized_zero_factor_is_zero_and_ten_thousand_is_identity()
    {
        var hit = DamagePacket.Of(DamageType.Physical, 10, 20).Add(DamageType.Fire, new DamageRange(3, 7));
        Assert.True(SpellDamage.WithEffectiveness(hit, 0).IsZero);
        Assert.Equal(hit.Components, SpellDamage.WithEffectiveness(hit, 10000).Components);
        Assert.Equal(new DamageRange(5, 10), SpellDamage.WithEffectiveness(hit, 5000)[DamageType.Physical]);
    }

    [Fact]
    public void Increases_that_apply_are_summed()
    {
        var hit = SpellDamage.Hit(Stats(("spell_minimum_base_fire_damage", 9), ("spell_maximum_base_fire_damage", 14),
            ("spell_damage_+%", 10), ("fire_damage_+%", 10), ("elemental_damage_+%", 5), ("damage_+%", 5),
            ("base_is_projectile", 1), ("projectile_damage_+%", 10),
            ("cold_damage_+%", 100), ("attack_damage_+%", 100)));   // other types and attacks do not count
        Assert.Equal(9 * 1.4, hit.Min, 6);
        Assert.Equal(14 * 1.4, hit.Max, 6);
    }

    [Fact]
    public void Projectile_damage_needs_a_projectile_skill()
    {
        Assert.Equal((9, 14), SpellDamage.Hit(Stats(("spell_minimum_base_fire_damage", 9), ("spell_maximum_base_fire_damage", 14),
            ("projectile_damage_+%", 50))));
    }

    [Fact]
    public void Skills_without_spell_damage_hit_for_nothing()
    {
        Assert.Equal((0, 0), SpellDamage.Hit(Stats(("spell_damage_+%", 50))));
    }

    [Fact]
    public void Damage_per_use_keeps_the_fractions()
    {
        // 9-14 with 22% increased: 10.98-17.08, average 14.03 (not (10 + 17) / 2 = 13.5).
        var hit = SpellDamage.Hit(Stats(("spell_minimum_base_fire_damage", 9), ("spell_maximum_base_fire_damage", 14), ("spell_damage_+%", 22)));
        Assert.Equal(1403, SpellDamage.Display(hit, 1).PerUse);
    }

    [Fact]
    public void Hits_roll_between_minimum_and_maximum_without_the_fraction()
    {
        var rng = new Random(5);
        var rolls = Enumerable.Range(0, 2000).Select(_ => SpellDamage.Roll((10.17, 15.82), rng)).ToList();
        Assert.Equal(10, rolls.Min());
        Assert.Equal(15, rolls.Max());
    }

    [Fact]
    public void Display_is_the_average_hit_and_that_times_casts_per_second()
    {
        // Fireball: 9-14, cast time 750 ms (1.33 casts per second).
        Assert.Equal((1150, 1533), SpellDamage.Display((9, 14), 100 / 75.0));
    }

    [Fact]
    public void Stat_keys_are_found_by_name_as_before()
    {
        var stats = new StatCatalog(Data());
        Assert.Equal(stats.Key("spell_damage_+%"), stats.Key("spell_damage_+%"));
        Assert.Equal("spell_damage_+%", stats.Name(stats.Key("spell_damage_+%")!.Value));
        Assert.Null(stats.Key("no_such_stat"));
        Assert.Equal(0x10u, stats.Key("0x10"));
    }

    static string Data() => "data";
}
