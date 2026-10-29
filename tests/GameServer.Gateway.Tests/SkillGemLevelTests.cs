namespace GameServer.Gateway.Tests;

public class SkillGemLevelTests
{
    [Theory]
    [InlineData(20, 0, 3, 23)]
    [InlineData(20, 2, 3, 25)]
    [InlineData(20, 2, -30, 1)]
    [InlineData(1, -10, 20, 11)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)]
    [InlineData(20, int.MaxValue, int.MinValue, 19)]
    public void Modifiers_sum_before_the_one_final_clamp(int intrinsic, int supported, int family, int expected) =>
        Assert.Equal(expected, SkillGemLevel.Resolve(intrinsic, supported, [family]));

    [Theory]
    [InlineData("Herald of Ash", "herald_skill_gem_level_+", true)]
    [InlineData("Herald of Agony", "herald_skill_gem_level_+", true)]
    [InlineData("Vaal Fireball", "vaal_skill_gem_level_+", true)]
    [InlineData("Soul Link", "link_skill_gem_level_+", true)]
    [InlineData("Raise Zombie", "raise_zombie_gem_level_+", true)]
    [InlineData("Raise Zombie of Slamming", "raise_zombie_gem_level_+", true)]
    [InlineData("Fireball", "vaal_skill_gem_level_+", false)]
    [InlineData("Fireball", "herald_skill_gem_level_+", false)]
    [InlineData("Raise Spectre", "raise_zombie_gem_level_+", false)]
    [InlineData("Fireball", "missing_stat", false)]
    public void Target_metadata_classifies_families_and_variants(string name, string stat, bool expected)
    {
        var type = Program.Items.FindGem(name)!.Value.Type;
        Assert.Equal(expected, Program.Skills.FamilyStatRaises(stat, type.Row));
        Assert.False(Program.Skills.FamilyStatRaises(stat, -1));
    }
}
