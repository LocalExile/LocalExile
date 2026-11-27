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
        /// <summary>Hands out the host's own list, as a host may: a hit that changes it changes what was handed out.</summary>
        public bool Live;
        public Action<ProjectileDamage>? OnHit;
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => Live ? Targets : Targets.ToArray();
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => Terrain(from, to);
        public ProjectileHitResult Hit(ProjectileDamage hit) { Hits.Add(hit); OnHit?.Invoke(hit); return ProjectileHitResult.Hit; }
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

    [Fact]
    public void Segment_hits_the_corridor_and_round_ends_at_the_distance_to_its_source()
    {
        var arena = new Arena();
        arena.Targets.AddRange(
        [
            new(new(1), 2, _ => new(30, 5), 0),
            new(new(2), 2, _ => new(30, 7), 0),
            new(new(3), 2, _ => new(14, 3), 1),
            new(new(4), 2, _ => new(14, 4), 1),
            new(new(5), 1, _ => new(30, 0), 0),
            new(new(6), 2, _ => new(45, 0), 0),
        ]);

        var runtime = new ProjectileRuntime(arena);
        runtime.ImpactAlong(Use(Profile), new(20, 0), new(40, 0), 6, "beam");

        Assert.Equal(new uint[] { 1, 3, 6 }, arena.Hits.Select(h => h.Target.Id));
        Assert.All(arena.Hits, hit =>
        {
            Assert.True(hit.IsArea);
            Assert.Equal(20, hit.Projectile.Distance);
        });
        Assert.Empty(runtime.Projectiles);
    }

    // An area decides its hits by actor id, then generation; a pulse by id, keeping the listed order of equal ids. Both
    // take the actors as they are before the first hit: a hit that kills changes the host's list. A host that lists
    // them in that order already is not sorted again (ProjectileRuntime.ActorsInOrder), and that changes none of it.
    [Fact]
    public void Areas_and_pulses_hit_in_key_order_from_the_actors_listed_before_their_first_hit()
    {
        static ProjectileActor At(uint id, int generation, double x) => new(new(id, generation), 2, _ => new(x, 0), 0);
        ProjectileActor[] ordered = [At(1, 0, 1), At(2, 0, 2), At(3, 1, 3), At(3, 2, 4), At(5, 0, 5), At(7, 0, 6)];
        ProjectileActor[] shuffled = [ordered[4], ordered[3], ordered[0], ordered[5], ordered[2], ordered[1]];
        // In id order, but the two generations of id 3 the other way round: in order for a pulse, not for an area.
        ProjectileActor[] byIdOnly = [ordered[0], ordered[1], ordered[3], ordered[2], ordered[4], ordered[5]];
        var pulse = new ProjectileProfile("pulse") { Speed = 20, Range = 40, Radius = 0, DirectDamage = false,
            Pulse = new("pulse", 100, T(1)) };
        foreach (var listing in new[] { ordered, shuffled, byIdOnly })
            foreach (bool live in new[] { false, true })
                foreach (bool kills in new[] { false, true })
                {
                    Arena World()
                    {
                        var w = new Arena { Live = live };
                        w.Targets.AddRange(listing);
                        // The hit actor leaves the host's list, and so does the first one still listed.
                        if (kills) w.OnHit = hit =>
                        {
                            w.Targets.RemoveAll(a => a.Key == hit.Target);
                            if (w.Targets.Count > 0) w.Targets.RemoveAt(0);
                        };
                        return w;
                    }
                    var area = World();
                    new ProjectileRuntime(area).Impact(Use(Profile), new(0, 0), new("blast", 100));
                    Assert.Equal(ordered.Select(a => a.Key), area.Hits.Select(h => h.Target));

                    var pulsed = World();
                    new ProjectileRuntime(pulsed).Release(Use(pulse));
                    Assert.Equal(listing.OrderBy(a => a.Key.Id).Select(a => a.Key), pulsed.Hits.Select(h => h.Target));
                }
    }
}
