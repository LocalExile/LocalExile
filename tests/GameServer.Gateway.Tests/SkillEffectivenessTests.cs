using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// GESSPL stores signed deltas from identity for two independent multipliers. PoB's exporter decodes raw/10000 +1:
// Flicker Strike's raw7730 means177.3%, not77.3%. The shared test initializer supplies repository-relative tables.
public class SkillEffectivenessTests
{
    static readonly SkillCatalog Skills = new();

    [Fact]
    public void An_attack_gems_effectiveness_is_its_levels_DamageEffectiveness()
    {
        int flicker = Skills.EffectRow("FlickerStrike");
        Assert.Equal(17730, Skills.DamageEffectiveness(flicker, 1));
        Assert.Equal(40890, Skills.DamageEffectiveness(flicker, 20));
        Assert.Equal(79600, Skills.DamageEffectiveness(flicker, 40));
    }

    [Fact]
    public void A_level_outside_the_rows_uses_the_closest_one()
    {
        // Flicker Strike has rows for levels 1 to 40 only: below 1 the lowest answers, above 40 the highest.
        int flicker = Skills.EffectRow("FlickerStrike");
        Assert.Equal(17730, Skills.DamageEffectiveness(flicker, 0));
        Assert.Equal(79600, Skills.DamageEffectiveness(flicker, 41));
    }

    [Fact]
    public void The_default_attack_and_unknown_effects_have_identity_factors()
    {
        // PlayerMelee's row holds 0: the default attack deals the weapon's hit as it is.
        Assert.Equal(10000, Skills.DamageEffectiveness(Skills.EffectRow("PlayerMelee"), 1));
        Assert.Equal(10000, Skills.DamageEffectiveness(-1, 1));
    }

    [Fact]
    public void A_weapon_hit_is_scaled_by_the_effectiveness()
    {
        var hit = DamagePacket.Of(DamageType.Physical, new DamageRange(10, 20));
        var scaled = SpellDamage.WithEffectiveness(hit, 17730)[DamageType.Physical];
        Assert.Equal(17.73, scaled.Min, 6);
        Assert.Equal(35.46, scaled.Max, 6);
        // An actual zero factor must stay distinguishable from missing data, whose decoded factor is identity.
        Assert.Same(hit, SpellDamage.WithEffectiveness(hit, 10_000));
        Assert.True(SpellDamage.WithEffectiveness(hit, 0).IsZero);
    }
}
