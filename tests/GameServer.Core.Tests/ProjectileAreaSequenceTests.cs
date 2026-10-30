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

}
