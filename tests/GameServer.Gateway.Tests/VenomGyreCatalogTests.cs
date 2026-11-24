namespace GameServer.Gateway.Tests;

public sealed class VenomGyreCatalogTests
{
    // 3.29 Projectiles.dat has no Metadata/Projectiles/VenomGyre row; the gem's row is ThrownWeapon, the
    // same row Spectral Throw uses (GrantedEffects VenomGyre row 10850, VaalVenomGyre row 12333;
    // Projectiles ThrownWeapon row 94, speed 1000). Without the fallback every press was refused as
    // having no projectile.
    [Fact]
    public void VenomGyreAndVaalVenomGyreResolveToThrownWeapon()
    {
        var skills = new SkillCatalog();
        int venomGyre = skills.EffectRow("VenomGyre");
        Assert.True(venomGyre >= 0);
        Assert.Equal(("Metadata/Projectiles/ThrownWeapon", 1000), skills.ProjectileFor(venomGyre));
        int vaalVenomGyre = skills.EffectRow("VaalVenomGyre");
        Assert.True(vaalVenomGyre >= 0);
        Assert.Equal(("Metadata/Projectiles/ThrownWeapon", 1000), skills.ProjectileFor(vaalVenomGyre));
    }
}
