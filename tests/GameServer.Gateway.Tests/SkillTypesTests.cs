namespace GameServer.Gateway.Tests;

// Which gem skills are attacks (ActiveSkillType Attack): they swing with the weapon's attack time, so a use is paced
// like the default attack and, out of reach, the player walks in before the swing. Reads the extracted game tables.
public class SkillTypesTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly SkillCatalog Skills = new(Data());

    [Fact]
    public void Attack_gems_are_attacks()
    {
        Assert.True(Skills.IsAttack(Skills.EffectRow("HolyStrike")));
        Assert.True(Skills.IsAttack(Skills.EffectRow("FlickerStrike")));
        Assert.True(Skills.IsAttack(Skills.EffectRow("GroundSlam")));
    }

    [Fact]
    public void The_default_attack_is_an_attack()
    {
        Assert.True(Skills.IsAttack(Skills.EffectRow("PlayerMelee")));
    }

    [Fact]
    public void Spells_and_auras_are_not_attacks()
    {
        Assert.False(Skills.IsAttack(Skills.EffectRow("Fireball")));
        Assert.False(Skills.IsAttack(Skills.EffectRow("RighteousFire")));
        Assert.False(Skills.IsAttack(Skills.EffectRow("Haste")));
    }

    [Fact]
    public void An_unknown_effect_is_not_an_attack()
    {
        Assert.False(Skills.IsAttack(-1));
    }
}
