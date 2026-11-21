namespace GameServer.Gateway.Tests;

public sealed class EtherealKnivesCatalogTests
{
    [Fact]
    public void EtherealKnivesResolvesItsProjectileDefinition()
    {
        var skills = new SkillCatalog();
        int ek = skills.EffectRow("EtherealKnives");
        Assert.True(ek >= 0);
        var projectile = skills.ProjectileFor(ek);
        Assert.NotNull(projectile);
        Assert.Equal("Metadata/Projectiles/EtherealKnivesProjectile", projectile!.Value.Id);
        Assert.Equal(2800, projectile.Value.Speed);
        foreach (var (quality, expectedSpeed) in new[] { (0, 257.6), (20, 309.12) })
        {
            var stats = skills.EffectStats(ek, 1, quality).ToDictionary(s => s.Item1, s => s.Item2);
            int Skill(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
            Assert.Equal(expectedSpeed, ProjectileSpeed.CellsPerSecond(projectile.Value.Speed, _ => 0, Skill), 3);
        }
    }
}
