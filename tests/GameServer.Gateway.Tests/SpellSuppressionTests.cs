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

    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }
}
