namespace GameServer.Gateway.Tests;

public class SpellSuppressionTests
{
    [Fact]
    public void Lucky_and_unlucky_suppression_flags_reach_the_pools()
    {
        var lucky = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(SpellSuppressionChance: 50, SpellSuppressionLucky: true));
        var unlucky = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(SpellSuppressionChance: 50, SpellSuppressionUnlucky: true));

        Assert.True(lucky.SpellSuppressionLucky);
        Assert.False(lucky.SpellSuppressionUnlucky);
        Assert.False(unlucky.SpellSuppressionLucky);
        Assert.True(unlucky.SpellSuppressionUnlucky);
        Assert.Equal(50, lucky.SpellSuppressionChance);
        Assert.Equal(50, unlucky.SpellSuppressionChance);
    }

    // Revenge of the Hunted (53114): +10% chance to suppress while on full life.
    [Theory]
    [InlineData(false, false, false, 30)]
    [InlineData(true, false, false, 40)]
    [InlineData(false, true, false, 35)]
    [InlineData(false, false, true, 50)]
    [InlineData(true, true, true, 65)]
    public void Conditional_suppression_counts_only_while_its_condition_holds(bool fullLife, bool moving, bool hitRecently, int chance)
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, new CharacterStats.Bonus(SpellSuppressionChance: 30,
            SpellSuppressionWhileFullLife: 10, SpellSuppressionWhileMoving: 5, SpellSuppressionIfHitRecently: 20));
        Assert.Equal(chance, CharacterStats.SuppressionChance(pools, fullLife, moving, hitRecently));
    }

    [Fact]
    public void Conditional_suppression_is_capped_at_100()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(SpellSuppressionChance: 95, SpellSuppressionWhileFullLife: 10));
        Assert.Equal(100, CharacterStats.SuppressionChance(pools, fullLife: true, moving: false, hitRecently: false));
    }

    [Fact]
    public void Revenge_of_the_hunted_carries_its_full_life_suppression()
    {
        var tree = new PassiveTree(Data());
        var names = new StatCatalog(Data());
        var stats = tree.Stats([53114]);
        Assert.Equal(10, names.Key("spell_suppression_chance_%_while_on_full_life") is uint k ? stats.GetValueOrDefault(k) : 0);
    }

    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RepositoryRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }
}
