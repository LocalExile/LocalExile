using GameServer.Core.Projectiles;

namespace GameServer.Core.Tests;

public class ProjectileExtensionTests
{
    [Fact]
    public void Count_consumers_apply_the_bonus_once_to_the_selected_stage()
    {
        var arrow = new ProjectileProfile("arrow");
        var modifiers = new ProjectileModifiers { ExtraProjectiles = 4, Chain = 2 };
        var ordinary = ProjectileParameters.Resolve(arrow, modifiers);
        Assert.Equal(5, ordinary.Count);
        var clustering = ProjectileParameters.Resolve(arrow with { CountConsumer = ProjectileCountConsumer.ContactAreas,
            ContactArea = new("blast", 10, 4) }, modifiers);
        Assert.Equal(1, clustering.Count);
        Assert.Equal(8, clustering.ContactArea!.Count);
        var bottle = ProjectileParameters.Resolve(arrow with { CountConsumer = ProjectileCountConsumer.GroundChains,
            GroundChains = 3 }, modifiers);
        Assert.Equal(1, bottle.Count);
        Assert.Equal(9, bottle.GroundChains);
    }

    [Fact]
    public void Exclusive_conversions_fail_explicitly_instead_of_using_registration_order()
    {
        Assert.Throws<InvalidOperationException>(() => ProjectileParameters.Resolve(new("skill"),
            new() { ExclusiveConversions = ["fury-valve", "arrow-nova"] }));
    }

    [Fact]
    public void Grid_corner_blocks_and_supplies_a_normal_for_reflection()
    {
        var hit = ProjectileGeometry.TraceGrid(new(0, 0), new(10, 10), (x, y) => x == 1 && y == 0);
        Assert.NotNull(hit);
        Assert.Equal(.05, hit.Value.Fraction, 8);
        Assert.True(hit.Value.Normal.X < 0);
    }

    sealed class Arena : IProjectileWorld
    {
        public List<ProjectileDamage> Hits = [];
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) =>
            [new(new(2), 2, _ => new(30, 0), 0)];
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => null;
        public ProjectileHitResult Hit(ProjectileDamage damage) { Hits.Add(damage); return ProjectileHitResult.Hit; }
        public void Publish(ProjectileEvent value) { }
    }

    [Fact]
    public void Wall_crossing_applies_a_payload_once_and_respects_atomic_capacity()
    {
        var world = new Arena(); var runtime = new ProjectileRuntime(world);
        runtime.Interactions.Add(new(1, ProjectileInteractionKind.Augment, new(15, -20), new(15, 20))
            { Team = 1, Capacity = 1, DamageMultiplier = 2 });
        var profile = new ProjectileProfile("arrow") { Count = 2, SpreadRadians = 0, Speed = 3000,
            HitRule = new(ProjectileHitScope.Projectile) };
        runtime.Release(new(profile, new(1), new(1), 1, new(0, 0), new(100, 0)));
        runtime.AdvanceTo(4000);
        Assert.Equal(new[] { 2d, 1d }, world.Hits.Select(h => h.Multiplier));
        Assert.Equal(0, runtime.Interactions.Single().Capacity);
    }

    [Fact]
    public void A_distance_form_change_keeps_entity_identity_and_can_grant_pierce()
    {
        var world = new Arena(); var runtime = new ProjectileRuntime(world);
        var profile = new ProjectileProfile("spear") { Speed = 3000, Range = 100,
            FormChange = new(20, 2, -1) };
        runtime.Release(new(profile, new(1), new(1), 1, new(0, 0), new(100, 0)));
        long id = Assert.Single(runtime.Projectiles).Id;
        runtime.AdvanceTo(1600);
        Assert.Equal(id, Assert.Single(runtime.Projectiles).Id);
        Assert.Equal(1, Assert.Single(runtime.Projectiles).Form);
        Assert.Single(world.Hits);
    }
}
