using GameServer.Core.Projectiles;

namespace GameServer.Core.Tests;

public class ProjectilePolicyTests
{
    sealed class Arena : IProjectileWorld
    {
        public List<ProjectileActor> Targets = [new(new(1), 1, _ => new(0, 0), 0)];
        public List<ProjectileDamage> Hits = [];
        public List<ProjectileEvent> Events = [];
        public Func<ProjectileDamage, ProjectileHitResult> Accept = _ => ProjectileHitResult.Hit;
        public Func<ProjectileVector, ProjectileVector, TerrainContact?> Terrain = (_, _) => null;
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => Targets;
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => Terrain(from, to);
        public ProjectileHitResult Hit(ProjectileDamage damage) { Hits.Add(damage); return Accept(damage); }
        public void Publish(ProjectileEvent value) => Events.Add(value);
        public void Target(uint id, double x, double y = 0) => Targets.Add(new(new(id), 2, _ => new(x, y), 0));
    }

    static ProjectileProfile Basic => new("synthetic") { Speed = 100, Range = 100, Radius = 1 };
    static ProjectileUse Use(ProjectileProfile p, int seed = 1) => new(p, new(1), new(1), 1, new(0, 0), new(100, 0))
        { ReturnAnchor = new(1), Seed = seed };

    [Theory]
    [InlineData(false)]
    public void Selected_endpoint_area_can_run_before_return_while_end_emission_stays_deferred(bool atTurnaround)
    {
        var world = new Arena(); var runtime = new ProjectileRuntime(world);
        var profile = Basic with
        {
            DirectDamage = false, Range = 20, Returns = true,
            EndArea = new("endpoint", 5),
            EndAreaOnReturnTurnaround = atTurnaround,
            EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Distance, ProjectileEndReason.Returned },
            EndEmission = new(Basic with { DirectDamage = false })
        };
        runtime.Release(Use(profile)); runtime.AdvanceTo(24000);
        Assert.Equal(1, Assert.Single(runtime.Projectiles).Phase);
        Assert.Equal(atTurnaround ? 1 : 0, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
        Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Spawn);
        runtime.AdvanceTo(24000); // Re-advancing the same boundary cannot duplicate the stop event.
        Assert.Equal(atTurnaround ? 1 : 0, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
        if (atTurnaround)
        {
            var areaIndex = world.Events.FindIndex(e => e.Kind == ProjectileEventKind.Area);
            Assert.Equal(20, world.Events[areaIndex].Position.X);
            Assert.True(areaIndex < world.Events.FindIndex(e => e.Kind == ProjectileEventKind.Redirect));
        }
        runtime.AdvanceTo(48000);
        Assert.Equal(atTurnaround ? 2 : 1, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
        Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Spawn && e.Projectile.ParentId != 0);
    }

}
