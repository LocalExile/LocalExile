using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class TornadoShotSecondaryTests
{
    sealed class Arena(bool wall) : IProjectileWorld
    {
        public readonly List<ProjectileActor> Targets = [];
        public readonly List<uint> Hits = [];
        public readonly List<long> Redirects = [];
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => Targets;
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) =>
            wall && from.X < 60 && to.X >= 60 ? new((60 - from.X) / (to.X - from.X), new(-1, 0)) : null;
        public ProjectileHitResult Hit(ProjectileDamage damage)
        {
            Hits.Add(damage.Target.Id);
            return ProjectileHitResult.Hit;
        }
        public void Publish(ProjectileEvent value)
        {
            if (value.Kind == ProjectileEventKind.Redirect) Redirects.Add(value.Projectile.Id);
        }
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
        foreach (var (grant, forbid, wall) in new[] { (0, 0, false), (1, 0, false), (1, 1, false), (1, 0, true) })
        {
            int Get(string key) => key switch
            {
                "number_of_chains" => 1,
                "projectile_chain_from_terrain_chance_%" => wall ? 100 : 0,
                "arrows_always_pierce_after_forking" => grant,
                "virtual_projectiles_cannot_pierce" => forbid,
                _ => 0
            };
            var arena = new Arena(wall);
            if (wall) arena.Targets.Add(new(new(50), 2, _ => new(40, 20), 0));
            var runtime = new ProjectileRuntime(arena);
            var profile = GatewayProjectileSkillProfiles.Resolve("TornadoShot",
                "Metadata/Projectiles/TornadoShotArrow", Get, 200, attack: true, arrows: true) with { Range = 100 };
            runtime.Release(new ProjectileUse(profile, new(999), new(999), 1, new(0, 0), new(100, 0)) { Sequence = 1 });
            var primary = Assert.Single(runtime.Projectiles);
            double sample = wall ? .95 : .65;
            runtime.AdvanceTo(ProjectileRuntime.Time(sample));

            var children = runtime.Projectiles.Where(p => p.ParentId != 0).OrderBy(p => p.Id).ToArray();
            Assert.Equal(3, children.Length);
            double expectedDistance = wall ? 190 : 130;
            Assert.All(children, child => Assert.InRange(child.Distance, expectedDistance - .1, expectedDistance + .1));
            if (wall)
            {
                Assert.Equal([primary.Id], arena.Redirects);
                Assert.Equal([50u], arena.Hits);
                arena.Redirects.Clear();
                arena.Hits.Clear();
            }
            // The secondary can pierce from its first hit only if the primary already chained off the wall.
            var arrow = children[0];
            for (uint id = 1; id <= 4; id++)
            {
                var point = arrow.Position + arrow.Direction * (id * 5);
                arena.Targets.Add(new(new(id), 2, _ => point, 0));
            }
            runtime.AdvanceTo(ProjectileRuntime.Time(sample + .3));
            Assert.Equal(wall ? Array.Empty<long>() : [arrow.Id], arena.Redirects);
            Assert.Equal(grant != 0 && forbid == 0 ? new uint[] { 1, 2, 3, 4 } : [1, 2], arena.Hits);
        }
    }
}
