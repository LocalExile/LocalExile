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

    [Fact]
    public void Per_initial_projectile_speeds_deliver_different_targets_in_same_time_window()
    {
        var world = new Arena();
        var runtime = new ProjectileRuntime(world);
        var profile = new ProjectileProfile("mortar")
        {
            Count = 2, Lobbed = true, DirectDamage = false,
            EndArea = new("landing", 2),
            EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing }
        };

        Assert.Throws<ArgumentException>(() => runtime.Release(Use(profile) with { ProjectileSpeeds = [10] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.Release(Use(profile) with { ProjectileSpeeds = [10, 0] }));

        var targets = new ProjectileVector[] { new(6, 0), new(24, 0) };
        var speeds = new[] { 6 / 0.633, 24 / 0.633 };
        runtime.Release(Use(profile) with { ProjectileTargets = targets, ProjectileSpeeds = speeds });
        runtime.AdvanceTo(ProjectileRuntime.Time(0.65));

        var landings = world.Events.Where(e => e.Kind == ProjectileEventKind.End && e.Reason == ProjectileEndReason.Landing).ToList();
        Assert.Equal(2, landings.Count);
        Assert.All(landings, l => Assert.InRange(l.At / (double)ProjectileRuntime.QuantaPerSecond, 0.62, 0.65));
        Assert.Contains(landings, l => Math.Abs(l.Position.X - 6) < 0.01);
        Assert.Contains(landings, l => Math.Abs(l.Position.X - 24) < 0.01);
    }

    [Fact]
    public void Ground_bounce_keeps_range_when_enabled_else_uses_ground_bounce_distance()
    {
        var world = new Arena();
        var runtime = new ProjectileRuntime(world);
        var profile = new ProjectileProfile("bounce")
        {
            Count = 1, Lobbed = true, DirectDamage = false, Speed = 100, GroundChains = 1, GroundBounceDistance = 16,
            EndArea = new("landing", 2),
            EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing }
        };

        runtime.Release(Use(profile with { GroundBounceKeepsRange = false }) with
        {
            ProjectileTargets = [new(10, 0)]
        });
        runtime.AdvanceTo(ProjectileRuntime.Time(1.0));
        var defaultEnd = Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End);
        Assert.Equal(26, defaultEnd.Position.X, 4);

        world.Events.Clear();
        runtime.Release(Use(profile with { GroundBounceKeepsRange = true }) with
        {
            ProjectileTargets = [new(10, 0)]
        });
        runtime.AdvanceTo(ProjectileRuntime.Time(2.0));
        var keptEnd = Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End);
        Assert.Equal(20, keptEnd.Position.X, 4);
    }
}
