using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// A skill's damage effectiveness (GrantedEffectStatSetsPerLevel DamageEffectiveness): the attack gem's "Deals X% of base
// Attack Damage", applied to the weapon's hit. Exported values are offsets from 100%, not absolute multipliers.
// Flicker Strike anchors it: 177.3% at level 1, 408.9% at 20 and 796% at 40
// in 3.29.3b. Reads the extracted game tables.
public class SkillEffectivenessTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly SkillCatalog Skills = new(Data());

    [Fact]
    public void An_attack_gems_effectiveness_is_its_levels_DamageEffectiveness()
    {
        int flicker = Skills.EffectRow("FlickerStrike");
        Assert.Equal(17730, Skills.DamageEffectiveness(flicker, 1));
        Assert.Equal(40890, Skills.DamageEffectiveness(flicker, 20));
        Assert.Equal(79600, Skills.DamageEffectiveness(flicker, 40));
    }

    [Theory]
    [InlineData("KineticBlast", 1, 14000)]
    [InlineData("KineticBlast", 20, 15500)]
    [InlineData("KineticBlastAltX", 1, 10000)]
    [InlineData("Cyclone", 1, 8160)]
    [InlineData("PowerSiphon", 1, 9000)]
    [InlineData("Fireball", 1, 30000)]
    public void Real_effectiveness_offsets_include_the_implicit_hundred_percent(string effect, int level, int expected)
    {
        Assert.Equal(expected, Skills.DamageEffectiveness(Skills.EffectRow(effect), level));
    }

    [Fact]
    public void Missing_effectiveness_keeps_the_existing_identity_fallback()
    {
        Assert.Equal(0, Skills.DamageEffectiveness(-1, 1));
        var hit = DamagePacket.Of(DamageType.Fire, 100, 100);
        Assert.Equal(100, SpellDamage.WithEffectiveness(hit, 0)[DamageType.Fire].Min);
    }

}
