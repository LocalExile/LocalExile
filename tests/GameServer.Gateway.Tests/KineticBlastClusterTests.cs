using GameServer.Core.Projectiles;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class KineticBlastClusterTests
{
    sealed class TestWorld(double placement) : IProjectileWorld
    {
        public List<ProjectileEvent> Events { get; } = [];
        public int PlacementTraces { get; private set; }
        public HashSet<int> Blocked { get; } = [];
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => [];
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to)
        {
            if (Math.Abs((to - from).Length - placement) > 1) return null; // flight segments are short
            return Blocked.Contains(PlacementTraces++) ? new TerrainContact(.5, new(-1, 0)) : null;
        }
        public ProjectileHitResult Hit(ProjectileDamage damage) => ProjectileHitResult.Hit;
        public void Publish(ProjectileEvent value) => Events.Add(value);
    }

    [Theory]
    [InlineData("KineticBlast", 14, 20, -40)]
    [InlineData("KineticBlastAltX", 19, 28, 0)]
    public void Kinetic_blast_clusters_use_the_fixed_radius_sector_retries_and_one_area_per_update(
        string effect, int blastRadius, int placement, int areaFinal)
    {
        var profile = GatewayProjectileSkillProfiles.Resolve(effect, "Metadata/Projectiles/WandProjectile", name => name switch
        {
            "cluster_burst_spawn_amount" => 4,
            "kinetic_blast_%_chance_for_additional_blast" => 100,
            "active_skill_base_area_of_effect_radius" => blastRadius,
            "active_skill_base_secondary_area_of_effect_radius" => placement,
            "active_skill_area_damage_+%_final" => areaFinal,
            _ => 0
        }, 100, attack: true);
        // Five sectors (4 + the 100% extra): all three candidates of sector 0 and the first of sector 1 are blocked.
        var world = new TestWorld(placement) { Blocked = { 0, 1, 2, 3 } };
        var runtime = new ProjectileRuntime(world);
        runtime.Release(new ProjectileUse(profile, new(10), new(10), 1, new(0, 0), new(100, 0)) { Seed = 7 });
        runtime.AdvanceTo(ProjectileRuntime.Time(5));

        var areas = world.Events.Where(e => e.Kind == ProjectileEventKind.Area).ToArray();
        Assert.Equal(3 + 2 + 1 + 1 + 1, world.PlacementTraces); // sector 0 omitted after three tests
        Assert.Equal(4, areas.Length); // the omitted sector leaves no slot
        var stop = areas[0].Projectile.Position;
        double width = Math.Tau / 5, unwrapped = 0, previous = double.NaN;
        long interval = ProjectileRuntime.Time(GatewayProjectileSkillProfiles.ClusterIntervalSeconds);
        for (int k = 0; k < areas.Length; k++)
        {
            var offset = areas[k].Position - stop;
            Assert.InRange(offset.Length, placement - 1e-6, placement + 1e-6); // no radial draw
            Assert.Equal(blastRadius, areas[k].Radius);
            // One area per update on the 1/30 s runtime cadence, overdue time kept: never early, under a tick late.
            Assert.InRange(areas[k].At - areas[0].At, k * interval, k * interval + ProjectileRuntime.TickQuanta - 1);
            double angle = Math.Atan2(offset.Y, offset.X);
            if (k > 0)
            {
                double step = (angle - previous + 2 * Math.Tau) % Math.Tau;
                Assert.InRange(step, 0, 2 * width); // the next sector, in order
                unwrapped += step;
            }
            previous = angle;
        }
        Assert.True(unwrapped < Math.Tau);
    }

    [Fact]
    public void Kinetic_blast_cluster_timer_emits_one_area_per_update_and_keeps_overdue_time()
    {
        long Q(double seconds) => ProjectileRuntime.Time(seconds);
        var timer = new ProjectileAreaTimer(0, Q(.1), 4);
        Assert.Equal(0, timer.Update(0)); // first update, even with no elapsed time
        Assert.Equal(1, timer.Update(Q(.35))); // a delayed update emits only one
        Assert.Equal(-Q(.15), timer.Timer);
        Assert.Equal(2, timer.Update(0)); // the debt is kept
        Assert.Equal(3, timer.Update(0));
        Assert.True(timer.Timer > 0);
        Assert.Null(timer.Update(0)); // a later update retires the group
        Assert.True(timer.Retired);
        var empty = new ProjectileAreaTimer(0, Q(.1), 0);
        Assert.Null(empty.Update(0));
        Assert.True(empty.Retired); // every sector omitted: retired on the first update
    }
}
