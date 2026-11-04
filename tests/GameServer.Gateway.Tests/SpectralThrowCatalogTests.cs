namespace GameServer.Gateway.Tests;

public sealed class SpectralThrowCatalogTests
{
    [Fact]
    public void SpectralThrowFiresTheThrownWeaponRow()
    {
        // Its Projectiles.dat row is not named after the effect; without the fallback every use was refused as having no
        // projectile while the client still threw (11-skills.md §10, Spectral Throw).
        var skills = new SkillCatalog();
        int effect = skills.EffectRow("SpectralThrow");
        Assert.True(effect >= 0);
        Assert.True(skills.IsAttack(effect));
        Assert.Equal(("Metadata/Projectiles/ThrownWeapon", 1000), skills.ProjectileFor(effect));
    }
}
