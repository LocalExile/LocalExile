using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class TornadoShotSecondaryTests
{
    // Tornado Shot's primary arrow pierces and releases three secondary arrows at its endpoint. The server simulates
    // them for damage but sends no objects because the client draws the same arrows itself, and one secondary per set
    // may hit an enemy.
    [Fact]
    public void Tornado_shot_primary_pierces_and_releases_hidden_secondaries()
    {
        static int Get(string _) => 0;
        var profile = GatewayProjectileSkillProfiles.Resolve("TornadoShot",
            "Metadata/Projectiles/TornadoShotArrow", Get, 200, attack: true);

        Assert.True(profile.Pierce > 0);
        Assert.NotNull(profile.EndEmission);
        Assert.True(profile.EndEmission!.Radial);
        Assert.True(profile.EndEmission.RandomPhase);
        Assert.Equal(3, profile.EndEmission.Child.Count);
        Assert.Equal("Metadata/Projectiles/TornadoShotSecondaryArrow", profile.EndEmission.Child.Id);
        Assert.True(profile.EndEmission.Child.HideVisual);
        Assert.True(profile.EndEmission.Child.Lifetime > 0);
        Assert.Equal(ProjectileHitScope.Emission, profile.EndEmission.Child.HitRule.Scope);
        Assert.Equal(ProjectileRepeat.Once, profile.EndEmission.Child.HitRule.Repeat);
        Assert.Null(profile.EndEmission.Child.EndEmission);
    }
}
