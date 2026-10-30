using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ChaosInoculationTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void Maximum_life_becomes_one_and_energy_shield_is_untouched()
    {
        var normal = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 50);
        var ci = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 50, new CharacterStats.Bonus(ChaosInoculation: true));

        Assert.True(normal.Life > 1);
        Assert.Equal(1u, ci.Life);
        Assert.Equal(normal.EnergyShield, ci.EnergyShield);
    }

}
