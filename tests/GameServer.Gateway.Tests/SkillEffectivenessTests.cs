using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// A skill's damage effectiveness (GrantedEffectStatSetsPerLevel DamageEffectiveness): the attack gem's "Deals X% of base
// Attack Damage", applied to the weapon's hit. Flicker Strike anchors it: 77.3% at level 1, 308.9% at 20 and 696% at 40
// in 3.29.3b. Reads the extracted game tables.
public class SkillEffectivenessTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly SkillCatalog Skills = new(Data());

    [Fact]
    public void An_attack_gems_effectiveness_is_its_levels_DamageEffectiveness()
    {
        int flicker = Skills.EffectRow("FlickerStrike");
        Assert.Equal(7730, Skills.DamageEffectiveness(flicker, 1));
        Assert.Equal(30890, Skills.DamageEffectiveness(flicker, 20));
        Assert.Equal(69600, Skills.DamageEffectiveness(flicker, 40));
    }

    [Fact]
    public void A_level_outside_the_rows_uses_the_closest_one()
    {
        // Flicker Strike has rows for levels 1 to 40 only: below 1 the lowest answers, above 40 the highest.
        int flicker = Skills.EffectRow("FlickerStrike");
        Assert.Equal(7730, Skills.DamageEffectiveness(flicker, 0));
        Assert.Equal(69600, Skills.DamageEffectiveness(flicker, 41));
    }

    [Fact]
    public void The_default_attack_and_unknown_effects_have_none()
    {
        // PlayerMelee's row holds 0: the default attack deals the weapon's hit as it is.
        Assert.Equal(0, Skills.DamageEffectiveness(Skills.EffectRow("PlayerMelee"), 1));
        Assert.Equal(0, Skills.DamageEffectiveness(-1, 1));
    }

    [Fact]
    public void A_weapon_hit_is_scaled_by_the_effectiveness()
    {
        var hit = DamagePacket.Of(DamageType.Physical, new DamageRange(10, 20));
        var scaled = SpellDamage.WithEffectiveness(hit, 7730)[DamageType.Physical];
        Assert.Equal(7.73, scaled.Min, 6);
        Assert.Equal(15.46, scaled.Max, 6);
        // 100% and no effectiveness both leave the hit as it is.
        Assert.Same(hit, SpellDamage.WithEffectiveness(hit, 10_000));
        Assert.Same(hit, SpellDamage.WithEffectiveness(hit, 0));
    }
}
