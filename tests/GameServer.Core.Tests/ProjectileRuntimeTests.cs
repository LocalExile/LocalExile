using GameServer.Core.Projectiles;

namespace GameServer.Core.Tests;

public class ProjectileRuntimeTests
{
    sealed class Arena : IProjectileWorld
    {
        public List<ProjectileActor> Targets = [];
        public List<ProjectileDamage> Hits = [];
        public List<ProjectileEvent> Events = [];
        public Func<ProjectileVector, ProjectileVector, TerrainContact?> Terrain = (_, _) => null;
        public Func<ProjectileDamage, ProjectileHitResult> Accept = _ => ProjectileHitResult.Hit;
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => Targets;
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => Terrain(from, to);
        public ProjectileHitResult Hit(ProjectileDamage damage)
        {
            var result = Accept(damage);
            if (result == ProjectileHitResult.Hit) Hits.Add(damage);
            return result;
        }
        public void Publish(ProjectileEvent value) => Events.Add(value);
        public void Target(uint id, double x, double y, int team = 2, int generation = 0) =>
            Targets.Add(new(new(id, generation), team, _ => new(x, y), 0));
    }

    static ProjectileProfile Arrow => new("arrow") { Speed = 3000, Range = 100, Radius = 1 };
    static ProjectileUse Use(ProjectileProfile profile, ProjectileVector? from = null, ProjectileVector? aim = null,
        int team = 1) => new(profile, new(1), new(1), team, from ?? new(0, 0), aim ?? new(100, 0));

    [Fact]
    public void Per_initial_projectile_payloads_preserve_shared_hit_admission_and_continuation_inheritance()
    {
        var world = new Arena(); world.Target(10, 30, 0); world.Target(20, 70, 0);
        var runtime = new ProjectileRuntime(world);
        var use = Use(Arrow with { Count = 2, Split = 1, Pierce = 0, SpreadRadians = 0 }) with { Payload = "default" };
        use = use with { ProjectilePayloads = ["paid", "unpaid"] };
        runtime.Release(use);
        Assert.Equal(new[] { "paid", "unpaid" }, runtime.Projectiles.Select(p => p.Use.Payload));
        Assert.Single(runtime.Projectiles.Select(p => p.EmissionId).Distinct());
        runtime.AdvanceTo(4000);
        Assert.Single(world.Hits, h => h.Target.Id == 10);
        Assert.Contains(world.Events, e => e.Projectile.ParentId != 0);
        Assert.All(world.Events.Where(e => e.Projectile.ParentId != 0), e =>
            Assert.Contains(e.Projectile.Use.Payload, new object?[] { "paid", "unpaid" }));
    }

}
