using GameServer.Core.Projectiles;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class KineticBlastClusterTests
{
    sealed class TestWorld : IProjectileWorld
    {
        public List<ProjectileEvent> Events { get; } = [];
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => [];
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => null;
        public ProjectileHitResult Hit(ProjectileDamage damage) => ProjectileHitResult.Hit;
        public void Publish(ProjectileEvent value) => Events.Add(value);
    }

    [Theory]
    [InlineData("KineticBlast", 4, 14, 20, -40)]
    [InlineData("KineticBlastAltX", 4, 19, 28, 0)]
    public void Kinetic_blast_clusters_scatter_inside_the_placement_radius_and_pop_unevenly(
        string effect, int count, int blastRadius, int placementRadius, int areaFinal)
    {
        var profile = GatewayProjectileSkillProfiles.Resolve(effect, "Metadata/Projectiles/WandProjectile", name => name switch
        {
            "cluster_burst_spawn_amount" => count,
            "active_skill_base_area_of_effect_radius" => blastRadius,
            "active_skill_base_secondary_area_of_effect_radius" => placementRadius,
            "active_skill_area_damage_+%_final" => areaFinal,
            _ => 0
        }, 100, attack: true);
        long interval = ProjectileRuntime.Time(GatewayProjectileSkillProfiles.ClusterIntervalSeconds);
        long jitter = ProjectileRuntime.Time(GatewayProjectileSkillProfiles.ClusterIntervalJitterSeconds);
        var offsets = new List<double>();
        for (int seed = 1; seed <= 5; seed++)
        {
            var world = new TestWorld();
            var runtime = new ProjectileRuntime(world);
            runtime.Release(new ProjectileUse(profile, new(10), new(10), 1, new(0, 0), new(100, 0)) { Seed = seed });
            runtime.AdvanceTo(ProjectileRuntime.Time(5));
            // The range stop fires one cluster; its centres are measured from that stop.
            var areas = world.Events.Where(e => e.Kind == ProjectileEventKind.Area).ToArray();
            Assert.Equal(count, areas.Length);
            var stop = areas[0].Projectile.Position;
            var distances = areas.Select(a => (a.Position - stop).Length).ToArray();
            Assert.All(distances, d => Assert.InRange(d, placementRadius / 2.0, placementRadius));
            Assert.All(areas, a => Assert.Equal(blastRadius, a.Radius));
            for (int i = 1; i < areas.Length; i++)
            {
                Assert.InRange(areas[i].At - areas[i - 1].At, interval, interval + jitter);
                // Clustering spirals outward: each blast lands farther out than the one before.
                if (effect == "KineticBlastAltX") Assert.True(distances[i] > distances[i - 1]);
            }
            offsets.AddRange(distances);
        }
        // Not a fixed ring: distances vary across the clusters.
        Assert.True(offsets.Max() - offsets.Min() > placementRadius / 4.0);
    }
}
