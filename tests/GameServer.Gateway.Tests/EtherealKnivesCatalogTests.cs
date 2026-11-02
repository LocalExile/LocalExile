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
    }
}
