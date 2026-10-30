using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class AccuracyTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void Base_accuracy_is_two_per_level_and_dexterity()
    {
        var level1 = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1);
        var level10 = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 10);

        // Witch dexterity 14: 2 * level + 2 * 14.
        Assert.Equal(30, level1.Accuracy);
        Assert.Equal(48, level10.Accuracy);
    }

}
