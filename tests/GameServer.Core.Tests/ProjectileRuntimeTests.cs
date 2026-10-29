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

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Per_initial_projectile_payload_count_must_match_the_initial_projectile_count(int count)
    {
        var runtime = new ProjectileRuntime(new Arena());
        var use = Use(Arrow with { Count = 2 });
        use = use with { ProjectilePayloads = new object?[count] };
        Assert.Throws<ArgumentException>(() => runtime.Release(use));
        Assert.Empty(runtime.Projectiles);
    }

    [Fact]
    public void Pierce_consumes_remaining_subtick_and_hits_both_targets_in_contact_order()
    {
        var world = new Arena(); world.Target(20, 70, 0); world.Target(10, 30, 0);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow with { Pierce = 1 }));
        runtime.AdvanceTo(4000);
        Assert.Equal(new uint[] { 10, 20 }, world.Hits.Select(h => h.Target.Id));
        Assert.InRange(world.Hits[0].At, 1159, 1161);
        Assert.InRange(world.Hits[1].At, 2759, 2761);
        Assert.Empty(runtime.Projectiles);
    }

    [Fact]
    public void Same_volley_direct_hits_share_quota_even_with_distinct_projectile_entities()
    {
        var world = new Arena(); world.Target(10, 30, 0);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow with { Count = 3, SpreadRadians = 0 }));
        runtime.AdvanceTo(4000);
        Assert.Single(world.Hits);
        Assert.Equal(3, world.Events.Count(e => e.Kind == ProjectileEventKind.Spawn));
    }

    [Fact]
    public void A_return_has_a_new_group_phase_but_does_not_turn_the_volley_into_shotgun()
    {
        var world = new Arena(); world.Target(10, 30, 0);
        world.Targets.Add(new(new(1), 1, _ => new(0, 0), 0));
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow with { Count = 3, SpreadRadians = 0, Pierce = -1, Returns = true })
            with { ReturnAnchor = new(1) });
        runtime.AdvanceTo(8000);
        Assert.Equal(2, world.Hits.Count);
        Assert.Equal(new[] { 0, 1 }, world.Hits.Select(h => h.Phase));
    }

    [Fact]
    public void All_flights_resolve_by_event_time_instead_of_projectile_iteration_order()
    {
        var world = new Arena(); world.Target(10, 80, 0); world.Target(20, 20, 20);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow));
        runtime.Release(Use(Arrow, new(0, 20), new(100, 20)));
        runtime.AdvanceTo(4000);
        Assert.Equal(new uint[] { 20, 10 }, world.Hits.Select(h => h.Target.Id));
    }

    [Fact]
    public void Advance_drains_all_contacts_at_its_inclusive_boundary()
    {
        var world = new Arena(); world.Target(10, 31, 0); world.Target(20, 31, 20);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow));
        runtime.Release(Use(Arrow, new(0, 20), new(100, 20)));
        runtime.AdvanceTo(1200);
        Assert.Equal(new uint[] { 10, 20 }, world.Hits.Select(h => h.Target.Id));
        Assert.Empty(runtime.Projectiles);
        runtime.AdvanceTo(1200);
        Assert.Equal(2, world.Hits.Count);
    }

    [Fact]
    public void Damage_rolls_cannot_reposition_explosions_and_reward_rolls_cannot_change_combat_rolls()
    {
        (ProjectileVector[] Centres, int CombatRoll) Areas(bool consume)
        {
            var world = new Arena(); world.Target(10, 30, 0); world.Target(20, 30, 1);
            var runtime = new ProjectileRuntime(world);
            world.Accept = hit =>
            {
                if (consume) for (int i = 0; i < 20; i++) hit.Projectile.RandomSource.Next();
                return ProjectileHitResult.Hit;
            };
            runtime.Release(Use(Arrow with { ContactArea = new("blast", 30, 4, 10) }));
            var p = Assert.Single(runtime.Projectiles);
            if (consume) for (int i = 0; i < 20; i++) p.RewardRandomSource.Next();
            int combatRoll = p.RandomSource.Next();
            runtime.AdvanceTo(4000);
            return (world.Events.Where(e => e.Kind == ProjectileEventKind.Area).Select(e => e.Position).ToArray(), combatRoll);
        }
        var baseline = Areas(false); var extraRolls = Areas(true);
        Assert.Equal(baseline.CombatRoll, extraRolls.CombatRoll);
        Assert.Equal(baseline.Centres, extraRolls.Centres);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void The_same_engine_handles_player_and_hostile_sources(int sourceTeam, int targetTeam)
    {
        var world = new Arena(); world.Target(10, 30, 0, targetTeam); world.Target(11, 10, 0, sourceTeam);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow, team: sourceTeam));
        runtime.AdvanceTo(4000);
        Assert.Equal(10u, Assert.Single(world.Hits).Target.Id);
    }

    [Fact]
    public void Moving_actor_can_intercept_between_tick_endpoints()
    {
        var world = new Arena();
        world.Targets.Add(new(new(10), 2, t => new(50, 30 - t * 60.0 / 4000), 1));
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow));
        runtime.AdvanceTo(4000);
        Assert.Single(world.Hits);
    }

    [Fact]
    public void Ledger_distinguishes_generations_and_window_boundaries()
    {
        var ledger = new ProjectileHitLedger();
        var rule = new ProjectileHitRule(ProjectileHitScope.Emission, ProjectileRepeat.Window, 79200);
        Assert.True(ledger.TryHit(1, 0, "direct", new(10, 1), 0, 0, rule));
        Assert.False(ledger.TryHit(1, 0, "direct", new(10, 1), 79199, 0, rule));
        Assert.True(ledger.TryHit(1, 0, "direct", new(10, 1), 79200, 0, rule));
        Assert.True(ledger.TryHit(1, 0, "direct", new(10, 2), 79200, 0, rule));
    }

    [Fact]
    public void Terminal_blast_is_one_hit_on_contact_victim_and_can_overlap_from_a_neighbor()
    {
        var world = new Arena(); world.Target(10, 30, 0); world.Target(20, 30, 10);
        var runtime = new ProjectileRuntime(world);
        var exploding = Arrow with { EndArea = new("blast", 15) };
        runtime.Release(Use(exploding));
        runtime.Release(Use(exploding, new(0, 10), new(100, 10)));
        runtime.AdvanceTo(4000);
        Assert.Equal(2, world.Hits.Count(h => h.Target.Id == 10));
        Assert.All(world.Hits, h => Assert.Equal("blast", h.Stage));
    }

    [Fact]
    public void A_piercing_projectile_has_direct_contact_then_only_one_terminal_blast()
    {
        var world = new Arena(); world.Target(10, 30, 0); world.Target(20, 70, 0);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow with { Pierce = 1, EndArea = new("blast", 15) }));
        runtime.AdvanceTo(4000);
        Assert.Equal(new[] { "direct", "blast" }, world.Hits.Select(h => h.Stage));
    }

    [Fact]
    public void Simultaneous_projectiles_cannot_shotgun_their_shared_contact_victim()
    {
        var world = new Arena(); world.Target(10, 30, 0);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow with { Count = 3, SpreadRadians = 0, EndArea = new("blast", 15) }));
        runtime.AdvanceTo(4000);
        Assert.Single(world.Hits);
    }

    [Fact]
    public void Contact_areas_can_overlap_independently_of_direct_volley_quota()
    {
        var world = new Arena(); world.Target(10, 30, 0);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow with { Count = 2, SpreadRadians = 0, ContactArea = new("contact", 15) }));
        runtime.AdvanceTo(4000);
        Assert.Equal(1, world.Hits.Count(h => h.Stage == "direct"));
        Assert.Equal(2, world.Hits.Count(h => h.Stage == "contact"));
    }

    [Fact]
    public void Hitless_parents_emit_children_with_a_separate_group_per_parent()
    {
        var world = new Arena(); world.Target(10, 70, 0);
        var runtime = new ProjectileRuntime(world);
        var child = Arrow with { Count = 3, SpreadRadians = 0 };
        var parent = Arrow with { DirectDamage = false, Count = 2, SpreadRadians = 0, Range = 40,
            EndEmission = new(child) };
        runtime.Release(Use(parent));
        runtime.AdvanceTo(4000);
        Assert.Equal(2, world.Hits.Count);
        Assert.All(world.Hits, h => Assert.NotEqual(0, h.Projectile.ParentId));
    }

    [Fact]
    public void Sensor_balls_share_a_rolling_interval_but_a_new_emission_is_independent()
    {
        var world = new Arena(); world.Target(10, 0, 0);
        var runtime = new ProjectileRuntime(world);
        var ball = Arrow with { Speed = 1, Count = 3, DirectDamage = false,
            Pulse = new("pulse", 20, 6000), HitRule = new(ProjectileHitScope.Emission, ProjectileRepeat.Interval, 18000) };
        runtime.Release(Use(ball));
        runtime.AdvanceTo(17999);
        Assert.Single(world.Hits);
        runtime.AdvanceTo(18000);
        Assert.Equal(2, world.Hits.Count);
        runtime.Release(Use(ball));
        runtime.AdvanceTo(24000);
        Assert.Equal(3, world.Hits.Count);
    }

    [Fact]
    public void Lobbed_extra_chain_lands_again_without_colliding_with_enemies_in_flight()
    {
        var world = new Arena(); world.Target(10, 30, 0);
        var runtime = new ProjectileRuntime(world);
        var bottle = Arrow with { Lobbed = true, Range = 30, GroundChains = 1, GroundBounceDistance = 0,
            EndArea = new("landing", 10) };
        runtime.Release(Use(bottle, aim: new(30, 0)));
        runtime.AdvanceTo(4000);
        Assert.Equal(2, world.Hits.Count);
        Assert.All(world.Hits, h => Assert.Equal("landing", h.Stage));
    }

    [Fact]
    public void Capability_prohibition_does_not_disable_other_continuations()
    {
        var world = new Arena(); world.Target(10, 30, 0); world.Target(20, 50, 20);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow with { CannotSplit = true, Split = 2, Chain = 1 }));
        runtime.AdvanceTo(4000);
        Assert.Equal(new uint[] { 10, 20 }, world.Hits.Select(h => h.Target.Id));
    }

    [Fact]
    public void Consuming_a_carrier_is_atomic_and_does_not_run_its_terminal_explosion()
    {
        var world = new Arena(); var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow with { EndArea = new("blast", 20) }));
        long carrier = Assert.Single(runtime.Projectiles).Id;
        Assert.True(runtime.TryConsume(carrier));
        Assert.False(runtime.TryConsume(carrier));
        Assert.DoesNotContain(world.Events, e => e.Kind == ProjectileEventKind.Area);
    }

    [Fact]
    public void Lightning_arrow_secondary_strikes_from_a_chain_contact_can_hit_the_initial_target_again()
    {
        var world = new Arena(); world.Target(10, 30, 0); world.Target(20, 60, 10);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow with { Chain = 1, ContactTargets = new("lightning-strike", 3, 40) }));
        runtime.AdvanceTo(4000);
        Assert.Equal(2, world.Hits.Count(h => h.Target.Id == 10));
        Assert.Contains(world.Hits, h => h.Target.Id == 10 && h.Stage == "lightning-strike");
    }

    [Fact]
    public void Terrain_returned_spark_can_hit_again_in_a_later_emission_window()
    {
        var world = new Arena(); world.Target(10, 30, 0);
        world.Terrain = (from, to) => to.X >= 50 && from.X < 50
            ? new TerrainContact((50 - from.X) / (to.X - from.X), new(-1, 0)) : null;
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow with { Speed = 100, Range = 200, Pierce = -1, TerrainBounces = 1,
            HitRule = new(ProjectileHitScope.Emission, ProjectileRepeat.Window, 60000) }));
        runtime.AdvanceTo(120000);
        Assert.Equal(2, world.Hits.Count);
    }

    [Fact]
    public void Sequential_ice_spears_are_distinct_emissions_and_can_each_hit_the_target()
    {
        var world = new Arena(); world.Target(10, 30, 0);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow with { Count = 3, SpreadRadians = 0, SequentialDelay = 1000 }));
        runtime.AdvanceTo(8000);
        Assert.Equal(3, world.Hits.Count);
        Assert.Equal(3, world.Hits.Select(h => h.Projectile.EmissionId).Distinct().Count());
    }

    [Fact]
    public void An_evaded_contact_does_not_consume_another_projectiles_direct_hit_quota()
    {
        var world = new Arena(); world.Target(10, 30, 0);
        int attempts = 0;
        world.Accept = _ => ++attempts == 1 ? ProjectileHitResult.Miss : ProjectileHitResult.Hit;
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow with { Count = 2, SpreadRadians = 0 }));
        runtime.AdvanceTo(4000);
        Assert.Single(world.Hits);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void Expiry_ends_the_original_flight_without_a_new_return_budget()
    {
        var world = new Arena(); world.Target(10, 15, 0);
        world.Targets.Add(new(new(1), 1, _ => new(0, 0), 0));
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Arrow with { Speed = 100, Range = 200, Lifetime = 24000,
            Pierce = -1, Returns = true }) with { ReturnAnchor = new(1) });
        runtime.AdvanceTo(60000);
        Assert.Single(world.Hits);
        Assert.DoesNotContain(world.Events, e => e.Kind == ProjectileEventKind.Redirect);
        Assert.Empty(runtime.Projectiles);
    }
}
