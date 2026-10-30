using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class SkillProjectileTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Theory]
    [InlineData("Fireball", "Metadata/Projectiles/Fireball")]
    public void A_projectile_row_named_after_the_effect_is_used(string effect, string expected)
    {
        var skills = new SkillCatalog(Data());
        int row = skills.EffectRow(effect);
        Assert.True(row >= 0);
        Assert.Equal(expected, skills.ProjectileFor(row)!.Value.Id);
    }

}
