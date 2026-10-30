namespace GameServer.Gateway.Tests;

public class ArmourEvasionTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void Armour_is_flat_armour_raised_by_increased_armour()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(Armour: 100, ArmourPct: 50));

        Assert.Equal(150, pools.Armour);
        Assert.Equal(50, pools.ArmourPct);
    }

}
