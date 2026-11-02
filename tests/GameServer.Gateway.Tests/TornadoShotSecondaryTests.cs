using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class TornadoShotSecondaryTests
{
    sealed class Arena : IProjectileWorld
    {
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => [];
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => null;
        public ProjectileHitResult Hit(ProjectileDamage damage) => ProjectileHitResult.Hit;
        public void Publish(ProjectileEvent value) { }
    }

    // Tornado Shot's primary arrow pierces and releases three secondary arrows at its endpoint. The server simulates
    // them for damage but sends no objects because the client draws the same arrows itself, and one secondary per set
    // may hit an enemy.
    [Fact]
    public void Tornado_shot_primary_pierces_and_releases_hidden_secondaries()
    {
        static int Get(string _) => 0;
        var profile = GatewayProjectileSkillProfiles.Resolve("TornadoShot",
            "Metadata/Projectiles/TornadoShotArrow", Get, 200, attack: true);

        Assert.True(profile.Pierce < 0);
        Assert.True(profile.HideVisual);
        Assert.NotNull(profile.EndEmission);
        Assert.True(profile.EndEmission!.Radial);
        Assert.NotNull(profile.EndEmission.BaseDirection);
        Assert.Equal(3, profile.EndEmission.Child.Count);
        Assert.Equal("Metadata/Projectiles/TornadoShotSecondaryArrow", profile.EndEmission.Child.Id);
        Assert.True(profile.EndEmission.Child.HideVisual);
        Assert.Equal(ProjectileRuntime.Time(.3), profile.EndEmission.Child.Lifetime);
        Assert.Equal(ProjectileHitScope.Emission, profile.EndEmission.Child.HitRule.Scope);
        Assert.Equal(ProjectileRepeat.Once, profile.EndEmission.Child.HitRule.Repeat);
        Assert.Null(profile.EndEmission.Child.EndEmission);

        // The client's terminal RNG vector [1,2,4096,0,0,0,0,0] samples 5185 (phi bits 0x4094ede8) and aims
        // along (sin phi, -cos phi); the server must produce the same base ray.
        var baseDirection = GatewayProjectileSkillProfiles.TornadoShotBaseDirection(1, 2, 4096, 0);
        Assert.Equal(-0.9982982277870178, baseDirection.X, 6);
        Assert.Equal(0.05831458792090416, baseDirection.Y, 6);
    }

    // Point Blank/Far Shot measure the whole continuation: a child starts with the primary's travelled distance and
    // adds its own, while its fixed lifetime means projectile speed scales how far it gets.
    [Fact]
    public void Tornado_shot_children_continue_the_primary_distance()
    {
        var runtime = new ProjectileRuntime(new Arena());
        var profile = GatewayProjectileSkillProfiles.Resolve("TornadoShot",
            "Metadata/Projectiles/TornadoShotArrow", _ => 0, 200, attack: true) with { Range = 100 };
        runtime.Release(new ProjectileUse(profile, new(999), new(999), 1, new(0, 0), new(100, 0))
            { Sequence = 1 });

        runtime.AdvanceTo(ProjectileRuntime.Time(.65));

        var children = runtime.Projectiles.Where(p => p.ParentId != 0).ToArray();
        Assert.Equal(3, children.Length);
        Assert.All(children, child => Assert.InRange(child.Distance, 129.9, 130.1));
    }
}
