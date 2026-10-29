using GameServer.Core.Projectiles;

namespace GameServer.Core.Tests;

public class ProjectileAreaSequenceTests
{
    sealed class Arena : IProjectileWorld
    {
        public List<ProjectileActor> Targets = [];
        public List<ProjectileEvent> Events = [];
        public List<ProjectileDamage> Hits = [];
        public Func<ProjectileVector, ProjectileVector, TerrainContact?> Terrain = (_, _) => null;
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => Targets.ToArray();
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => Terrain(from, to);
        public ProjectileHitResult Hit(ProjectileDamage hit) { Hits.Add(hit); return ProjectileHitResult.Hit; }
        public void Publish(ProjectileEvent value) => Events.Add(value);
        public void Target(uint id, double x) => Targets.Add(new(new(id), 2, _ => new(x, 0), 0));
    }

    static long T(double seconds) => ProjectileRuntime.Time(seconds);
    static ProjectileUse Use(ProjectileProfile profile) => new(profile, new(100), new(100), 1, new(0, 0), new(100, 0));
    static ProjectileProfile Profile => new("sequence") { Speed = 100, Range = 10, Radius = 0,
        DirectDamage = false, EndArea = new("blast", 2, 3) { Delay = T(.05), Interval = T(.05) } };

    [Theory]
    [InlineData(5, 0)]
    [InlineData(5, 4000)]
    [InlineData(2, 0)]
    [InlineData(2, 4000)]
    [InlineData(15, 0)]
    [InlineData(15, 4000)]
    public void Blocked_centres_are_not_resampled_and_do_not_reserve_area_times(int blockedMask, long delay)
    {
        var w = new Arena(); var centres = new List<ProjectileVector>();
        w.Targets.Add(new(new(1), 2, _ => new(10, 50), 0));
        w.Terrain = (from, to) =>
        {
            if ((to - from).Length < 19) return null; // The flight is 10 cells; placement paths are 20.
            int slot = centres.Count; centres.Add(to);
            return (blockedMask & (1 << slot)) != 0 ? new TerrainContact(.5, new(-1, 0)) : null;
        };
        var runtime = new ProjectileRuntime(w);
        runtime.Release(Use(Profile with { EndArea = new("blast", 100, 4, 20)
            { MinimumOffset = 20, SectorPlacement = true, RespectTerrain = true, Delay = delay, Interval = 4000 } }));
        runtime.AdvanceTo(T(.5));
        Assert.Equal(4, centres.Count);
        var areas = w.Events.Where(e => e.Kind == ProjectileEventKind.Area).ToArray();
        Assert.Equal(centres.Where((_, i) => (blockedMask & (1 << i)) == 0), areas.Select(e => e.Position));
        var times = Enumerable.Range(0, areas.Length).Select(i => T(.1) + delay + i * 4000).ToArray();
        Assert.Equal(times, areas.Select(e => e.At));
        Assert.Equal(times, w.Hits.Select(h => h.At));
        Assert.All(w.Hits, h => Assert.True(h.IsArea));
    }

    [Fact]
    public void Areas_survive_parent_end_at_exact_times_and_query_targets_at_each_detonation()
    {
        var w = new Arena(); w.Target(1, 10);
        var runtime = new ProjectileRuntime(w); runtime.Release(Use(Profile));
        runtime.AdvanceTo(T(.1));
        Assert.Empty(runtime.Projectiles); Assert.Empty(w.Hits);
        runtime.AdvanceTo(T(.15) - 1); Assert.Empty(w.Hits);
        runtime.AdvanceTo(T(.15)); Assert.Equal(1u, Assert.Single(w.Hits).Target.Id);
        w.Targets.Clear(); w.Target(2, 10);
        runtime.AdvanceTo(T(.25));
        Assert.Equal(new[] { T(.15), T(.2), T(.25) }, w.Hits.Select(h => h.At));
        Assert.Equal(new uint[] { 1, 2, 2 }, w.Hits.Select(h => h.Target.Id));
        Assert.All(w.Hits, h => Assert.True(h.IsArea));
        Assert.Equal(w.Hits.Select(h => h.At), w.Events.Where(e => e.Kind == ProjectileEventKind.Area).Select(e => e.At));
    }

    [Fact]
    public void Delayed_areas_keep_trigger_origin_phase_and_multiplier_while_parent_returns()
    {
        var w = new Arena(); var owner = new ProjectileActorKey(100);
        w.Targets.Add(new(owner, 1, _ => new(0, 0), 0)); w.Target(1, 10);
        var runtime = new ProjectileRuntime(w);
        runtime.Release(Use(Profile with { Returns = true, ReturnOvershoot = 100, EndAreaOnReturnTurnaround = true,
            EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Distance } }) with { ReturnAnchor = owner });
        runtime.AdvanceTo(T(.25));
        Assert.Equal(1, Assert.Single(runtime.Projectiles).Phase);
        Assert.All(w.Hits, h => { Assert.Equal(0, h.Phase); Assert.Equal(new ProjectileVector(10, 0), h.Position); });
        Assert.Equal(3, w.Hits.Count);
        Assert.All(w.Events.Where(e => e.Kind == ProjectileEventKind.Area), e => Assert.Equal(new ProjectileVector(10, 0), e.Position));
    }

    [Fact]
    public void Clear_cancels_pending_areas_after_parent_has_ended()
    {
        var w = new Arena(); w.Target(1, 10);
        var runtime = new ProjectileRuntime(w); runtime.Release(Use(Profile)); runtime.AdvanceTo(T(.1));
        runtime.Clear(); runtime.AdvanceTo(T(1));
        Assert.Empty(w.Hits); Assert.DoesNotContain(w.Events, e => e.Kind == ProjectileEventKind.Area);
    }

    [Fact]
    public void Pending_terminal_area_retains_direct_victim_admission_between_advances()
    {
        var w = new Arena(); w.Target(1, 10); w.Target(2, 11);
        var runtime = new ProjectileRuntime(w);
        runtime.Release(Use(Profile with { DirectDamage = true, EndArea = Profile.EndArea! with { Count = 1 } }));
        runtime.AdvanceTo(T(.1));
        Assert.Single(w.Hits, h => h.Target.Id == 1);
        runtime.AdvanceTo(T(.15));
        Assert.Single(w.Hits, h => h.Target.Id == 1);
        Assert.Single(w.Hits, h => h.Target.Id == 2);
    }
}
