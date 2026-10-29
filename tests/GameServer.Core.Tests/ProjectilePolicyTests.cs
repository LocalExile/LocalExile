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
    [InlineData(true)]
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

    [Fact]
    public void Turnaround_area_respects_the_selected_stop_reasons()
    {
        var world = new Arena(); var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with
        {
            DirectDamage = false, Range = 20, Returns = true, EndArea = new("end", 5),
            EndAreaOnReturnTurnaround = true,
            EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Returned }
        }));
        runtime.AdvanceTo(24000);
        Assert.DoesNotContain(world.Events, e => e.Kind == ProjectileEventKind.Area);
        runtime.AdvanceTo(48000);
        Assert.Equal(0, Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Area).Position.X);
    }

    [Fact]
    public void Excluded_contact_area_hits_neighbors_without_doubling_the_direct_victim()
    {
        var world = new Arena(); world.Target(2, 30); world.Target(3, 30, 5);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { ContactArea = new("contact", 10) { ExcludeContactTarget = true } }));
        runtime.AdvanceTo(120000);
        Assert.Equal(new[] { (2u, "direct"), (3u, "contact") }, world.Hits.Select(h => (h.Target.Id, h.Stage)));
    }

    [Fact]
    public void Filtered_end_area_keeps_direct_contact_and_empty_flight_area_and_end_emission()
    {
        var world = new Arena(); world.Target(2, 30);
        var runtime = new ProjectileRuntime(world);
        var profile = Basic with { EndArea = new("end", 10), EndAreaReasons = new HashSet<ProjectileEndReason>
            { ProjectileEndReason.Distance, ProjectileEndReason.Lifetime }, EndEmission = new(Basic with { DirectDamage = false }) };
        runtime.Release(Use(profile)); runtime.AdvanceTo(40000);
        Assert.Equal("direct", Assert.Single(world.Hits).Stage);
        Assert.DoesNotContain(world.Events, e => e.Kind == ProjectileEventKind.Area);
        Assert.Contains(world.Events, e => e.Kind == ProjectileEventKind.Spawn && e.Projectile.ParentId != 0);
        var empty = new Arena(); var flight = new ProjectileRuntime(empty);
        flight.Release(Use(profile)); flight.AdvanceTo(124000);
        Assert.Single(empty.Events, e => e.Kind == ProjectileEventKind.Area);
    }

    [Fact]
    public void Contact_area_can_be_outbound_only_while_terrain_contacts_still_emit_it()
    {
        var world = new Arena(); world.Target(2, 30);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Pierce = -1, Returns = true, ContactArea = new("contact", 1), ContactAreaOnReturn = false }));
        runtime.AdvanceTo(240000);
        Assert.Equal(2, world.Hits.Count(h => h.Stage == "direct"));
        Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Area);
        var wall = new Arena { Terrain = (from, to) => from.X < 50 && to.X >= 50
            ? new((50 - from.X) / (to.X - from.X), new(-1, 0)) : null };
        var terrain = new ProjectileRuntime(wall);
        terrain.Release(Use(Basic with { ContactArea = new("contact", 1), ContactAreaOnReturn = false }));
        terrain.AdvanceTo(120000);
        Assert.Single(wall.Events, e => e.Kind == ProjectileEventKind.Area);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(100, true)]
    public void Return_probability_extremes_and_prohibition_are_resolved_at_release(int chance, bool expected)
    {
        var runtime = new ProjectileRuntime(new Arena());
        runtime.Release(Use(Basic with { Count = 4, ReturnChancePercent = chance }));
        Assert.All(runtime.Projectiles, p => Assert.Equal(expected, p.Profile.Returns));
        runtime.Release(Use(Basic with { Returns = true, ReturnChancePercent = chance, CannotReturn = true }));
        Assert.False(runtime.Projectiles.Last().Profile.Returns);
    }

    [Fact]
    public void Partial_return_roll_is_shared_by_the_volley_repeatable_and_inherited_by_continuations()
    {
        bool[] Outcomes() => Enumerable.Range(1, 30).Select(seed =>
        {
            var world = new Arena(); world.Target(2, 30); world.Target(3, 50, 10);
            var runtime = new ProjectileRuntime(world);
            runtime.Release(Use(Basic with { Count = 4, SpreadRadians = 0, Split = 1, ReturnChancePercent = 50 }, seed));
            bool selected = runtime.Projectiles.First().Profile.Returns;
            Assert.All(runtime.Projectiles, p => Assert.Equal(selected, p.Profile.Returns));
            runtime.AdvanceTo(36000);
            Assert.All(world.Events.Where(e => e.Kind == ProjectileEventKind.Spawn), e => Assert.Equal(selected, e.Projectile.Profile.Returns));
            return selected;
        }).ToArray();
        var outcomes = Outcomes(); Assert.Contains(true, outcomes); Assert.Contains(false, outcomes);
        Assert.Equal(outcomes, Outcomes());
    }

    [Fact]
    public void Emitted_stages_resolve_their_own_chance_against_one_shared_use_sample()
    {
        var outcomes = new List<bool>();
        for (int seed = 1; seed <= 30; seed++)
        {
            var world = new Arena(); var runtime = new ProjectileRuntime(world);
            var child = Basic with { Count = 3, ReturnChancePercent = 50 };
            runtime.Release(Use(Basic with { DirectDamage = false, Range = 10,
                EndEmission = new(child), TravelEmission = new(child with { ReturnChancePercent = 100 }) }, seed));
            var parent = Assert.Single(runtime.Projectiles);
            bool expected = parent.Profile.EndEmission!.Child.Returns;
            Assert.True(parent.Profile.TravelEmission!.Child.Returns);
            runtime.AdvanceTo(16000);
            var children = runtime.Projectiles.Where(p => p.ParentId != 0).ToArray();
            Assert.Equal(3, children.Length);
            Assert.All(children, p => Assert.Equal(expected, p.Profile.Returns));
            outcomes.Add(expected);
        }
        Assert.Contains(true, outcomes); Assert.Contains(false, outcomes);
        var guaranteedChild = new ProjectileRuntime(new Arena());
        guaranteedChild.Release(Use(Basic with { EndEmission = new(Basic with { ReturnChancePercent = 100 }) }));
        Assert.True(Assert.Single(guaranteedChild.Projectiles).Profile.EndEmission!.Child.Returns);
    }

    [Fact]
    public void Return_applies_speed_once_and_overshoots_the_anchor()
    {
        var world = new Arena(); var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Returns = true, ReturnSpeedMultiplier = 2, ReturnOvershoot = 150 }));
        runtime.AdvanceTo(124000);
        var projectile = Assert.Single(runtime.Projectiles);
        Assert.Equal(200, projectile.Speed);
        Assert.Equal(-150, projectile.Position.X + projectile.Direction.X * projectile.Remaining, 6);
        runtime.AdvanceTo(280000);
        Assert.Empty(runtime.Projectiles);
        var end = Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End);
        Assert.Equal(-150, end.Position.X, 6); Assert.Equal(ProjectileEndReason.Returned, end.Reason);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void Turnaround_victim_can_be_excluded_without_losing_its_outbound_hit(bool exclude, int returnHits)
    {
        var world = new Arena(); world.Target(2, 30); var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Returns = true, ExcludeTurnaroundTarget = exclude }));
        runtime.AdvanceTo(120000);
        Assert.Single(world.Hits, h => h.Phase == 0);
        Assert.Equal(returnHits, world.Hits.Count(h => h.Phase == 1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(null, 3)]
    public void Return_contact_budget_is_independent_of_outbound_pierce(int? limit, int hits)
    {
        var world = new Arena(); world.Target(2, 20); world.Target(3, 40); world.Target(4, 60);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Pierce = -1, Returns = true, ReturnHitLimit = limit }));
        runtime.AdvanceTo(240000);
        Assert.Equal(3, world.Hits.Count(h => h.Phase == 0));
        Assert.Equal(hits, world.Hits.Count(h => h.Phase == 1));
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 2)]
    [InlineData(-1, 0)]
    public void Explicit_outbound_contact_budget_stops_attempts_but_returns_from_the_range_endpoint(int pierce, int limit)
    {
        var world = new Arena(); world.Target(2, 20); world.Target(3, 40); world.Target(4, 60);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Pierce = pierce, OutboundHitLimit = limit, Returns = true,
            ReturnHitLimit = 0, ReturnOvershoot = 150, ContactArea = new("contact", 1) { ExcludeContactTarget = true } }));
        runtime.AdvanceTo(124000);
        Assert.Equal(limit == 0 ? Array.Empty<uint>() : [2u, 3u], world.Hits.Select(h => h.Target.Id));
        Assert.Equal(limit, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
        var turnaround = Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Redirect);
        Assert.Equal(100, turnaround.Position.X, 6);
        Assert.Equal(1, Assert.Single(runtime.Projectiles).Phase);
        runtime.AdvanceTo(480000);
        var end = Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End);
        Assert.Equal(ProjectileEndReason.Returned, end.Reason);
        Assert.Equal(-150, end.Position.X, 6);
    }

    [Fact]
    public void Negative_outbound_contact_budget_is_rejected_before_spawning()
    {
        var runtime = new ProjectileRuntime(new Arena());
        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.Release(Use(Basic with { OutboundHitLimit = -1 })));
        Assert.Empty(runtime.Projectiles);
    }

    [Theory]
    [InlineData(false, false, 2)]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 2)]
    public void Terminal_area_can_require_an_outbound_flight_without_actor_contacts(bool terrain, bool empty, int areas)
    {
        var world = new Arena();
        if (!empty) { world.Target(2, 20); world.Target(3, 40); }
        if (terrain) world.Terrain = (from, to) => from.X < 80 && to.X >= 80
            ? new((80 - from.X) / (to.X - from.X), new(-1, 0)) : null;
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Pierce = -1, TerminalAreaRequiresNoContact = true,
            ContactArea = new("contact", 1) { ExcludeContactTarget = true }, EndArea = new("end", 1) }));
        runtime.AdvanceTo(124000);
        Assert.Equal(areas, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
        if (!empty) Assert.All(world.Events.Where(e => e.Kind == ProjectileEventKind.Area), e => Assert.True(e.Position.X < 50));
    }

    [Fact]
    public void Terminal_no_contact_requirement_is_scoped_to_the_outbound_phase()
    {
        var world = new Arena(); world.Target(2, 20); world.Target(3, 40);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Pierce = -1, Returns = true, TerminalAreaRequiresNoContact = true,
            ContactArea = new("contact", 1) { ExcludeContactTarget = true }, EndArea = new("end", 1) }));
        runtime.AdvanceTo(244000);
        Assert.Equal(5, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
        Assert.Contains(world.Events, e => e.Kind == ProjectileEventKind.Area && e.Position.X == 0);
    }

    [Fact]
    public void Exhausted_return_contact_budget_keeps_flying_to_the_overshoot_endpoint()
    {
        var world = new Arena(); world.Target(2, 20); world.Target(3, 40);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Pierce = -1, Returns = true, ReturnHitLimit = 1, ReturnOvershoot = 150,
            EndArea = new("end", 5), EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Returned } }));
        runtime.AdvanceTo(480000);
        Assert.Single(world.Hits, h => h.Phase == 1 && h.Stage == "direct");
        var end = Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End);
        Assert.Equal(ProjectileEndReason.Returned, end.Reason);
        Assert.Equal(-150, end.Position.X, 6);
        Assert.Equal(-150, Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Area).Position.X, 6);
    }

    [Fact]
    public void Shared_return_quota_denial_does_not_spend_another_projectiles_budget_or_emit_its_area()
    {
        var world = new Arena(); world.Target(2, 20); world.Target(3, 40);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Count = 2, SpreadRadians = 0, Pierce = -1, Returns = true,
            ReturnHitLimit = 1, ReturnOvershoot = 150, ContactArea = new("contact", 1) { ExcludeContactTarget = true } }));
        runtime.AdvanceTo(480000);
        var returns = world.Hits.Where(h => h.Phase == 1 && h.Stage == "direct").ToArray();
        Assert.Equal(new uint[] { 3, 2 }, returns.Select(h => h.Target.Id));
        Assert.Equal(2, returns.Select(h => h.Projectile.Id).Distinct().Count());
        Assert.Equal(2, world.Events.Count(e => e.Kind == ProjectileEventKind.Area && e.At > 124000));
        Assert.Equal(2, world.Events.Count(e => e.Kind == ProjectileEventKind.End && e.Reason == ProjectileEndReason.Returned));
        Assert.All(world.Events.Where(e => e.Kind == ProjectileEventKind.End), e => Assert.Equal(-150, e.Position.X, 6));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Return_hit_rule_can_share_volley_quota_while_outbound_hits_remain_per_projectile(bool sharedReturn)
    {
        var world = new Arena(); world.Target(2, 20); world.Target(3, 40);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Count = 2, SpreadRadians = 0, Pierce = -1, Returns = true,
            HitRule = new(ProjectileHitScope.Projectile), ReturnHitRule = sharedReturn ? new() : null,
            ReturnHitLimit = 1, ReturnOvershoot = 150, ContactArea = new("contact", 1) { ExcludeContactTarget = true } }));
        runtime.AdvanceTo(480000);
        Assert.Equal(new uint[] { 2, 2, 3, 3 }, world.Hits.Where(h => h.Phase == 0 && h.Stage == "direct").Select(h => h.Target.Id));
        Assert.Equal(sharedReturn ? new uint[] { 3, 2 } : [3, 3],
            world.Hits.Where(h => h.Phase == 1 && h.Stage == "direct").Select(h => h.Target.Id));
        Assert.Equal(2, world.Events.Count(e => e.Kind == ProjectileEventKind.Area && e.At > 124000));
        Assert.Equal(2, world.Events.Count(e => e.Kind == ProjectileEventKind.End && e.Reason == ProjectileEndReason.Returned));
    }

    [Fact]
    public void Return_direct_hit_rule_does_not_replace_pulse_admission()
    {
        var world = new Arena(); world.Target(2, 30);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Count = 2, SpreadRadians = 0, DirectDamage = false, Returns = true,
            HitRule = new(ProjectileHitScope.Projectile), ReturnHitRule = new(), Pulse = new("pulse", 200, 60000) }));
        runtime.AdvanceTo(180000);
        Assert.Equal(2, world.Hits.Count(h => h.Phase == 1 && h.Stage == "pulse"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outbound_denied_contact_skip_can_let_a_sibling_reach_the_farther_target(bool skip)
    {
        var world = new Arena(); world.Target(2, 20); world.Target(3, 40);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Count = 2, SpreadRadians = 0, SkipDeniedContacts = skip,
            ContactArea = new("contact", 1) { ExcludeContactTarget = true } }));
        runtime.AdvanceTo(124000);
        Assert.Equal(skip ? new uint[] { 2, 3 } : [2], world.Hits.Where(h => h.Stage == "direct").Select(h => h.Target.Id));
        Assert.Equal(skip ? new double[] { 19, 39 } : [19, 19],
            world.Events.Where(e => e.Kind == ProjectileEventKind.Area).Select(e => Math.Round(e.Position.X)));
        Assert.Equal(skip ? new double[] { 19, 39 } : [19, 19],
            world.Events.Where(e => e.Kind == ProjectileEventKind.End).Select(e => Math.Round(e.Position.X)));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void Unlimited_return_contact_areas_keep_staged_defaults_unless_denied_contacts_are_explicitly_skipped(
        bool skip, int areas)
    {
        var world = new Arena(); world.Target(2, 40);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Count = 2, SpreadRadians = 0, Pierce = -1, Returns = true,
            SkipDeniedReturnContacts = skip, ContactArea = new("contact", 1) }));
        runtime.AdvanceTo(244000);
        Assert.Single(world.Hits, h => h.Phase == 1 && h.Stage == "direct");
        Assert.Equal(areas, world.Events.Count(e => e.Kind == ProjectileEventKind.Area && e.At > 124000));
    }

    [Fact]
    public void Admitted_blocks_spend_return_budget_and_shared_quota()
    {
        var world = new Arena { Accept = h => h.Phase == 1 ? ProjectileHitResult.Blocked : ProjectileHitResult.Hit };
        world.Target(2, 20); world.Target(3, 40);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Count = 2, SpreadRadians = 0, Pierce = -1, Returns = true, ReturnHitLimit = 1 }));
        runtime.AdvanceTo(244000);
        Assert.Equal(new uint[] { 3, 2 }, world.Hits.Where(h => h.Phase == 1).Select(h => h.Target.Id));
        Assert.DoesNotContain(world.Events, e => e.Kind == ProjectileEventKind.Hit && e.At > 124000);
    }

    [Fact]
    public void Turnaround_exclusion_remains_when_a_repeatable_victim_moves_back_across_the_return()
    {
        var world = new Arena();
        world.Targets.Add(new(new(2), 2, t => new(t < 36000 ? 30 : 10, 0), 0));
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Returns = true, ExcludeTurnaroundTarget = true,
            HitRule = new(ProjectileHitScope.Emission, ProjectileRepeat.Window, 1000) }));
        runtime.AdvanceTo(120000);
        Assert.Single(world.Hits);
    }

    [Fact]
    public void Explicit_fan_preserves_fixed_gap_and_edge_angles_when_spread_exceeds_a_circle()
    {
        var runtime = new ProjectileRuntime(new Arena());
        runtime.Release(Use(Basic with { Count = 20, SpreadRadians = 19 * Math.PI / 2, CircularSpread = false }));
        var angles = runtime.Projectiles.Select(p => Math.Round(Math.Atan2(p.Direction.Y, p.Direction.X) * 180 / Math.PI, 6)).ToArray();
        Assert.Equal(new double[] { -135, -45, 45, 135, -135, -45, 45, 135, -135, -45,
            45, 135, -135, -45, 45, 135, -135, -45, 45, 135 }, angles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collision_only_return_does_not_trigger_at_maximum_distance(bool collision)
    {
        var world = new Arena(); if (collision) world.Target(2, 30);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Returns = true, ReturnOnDistance = false })); runtime.AdvanceTo(124000);
        Assert.Equal(collision, world.Events.Any(e => e.Kind == ProjectileEventKind.Redirect));
    }

    [Theory]
    [InlineData(false, ProjectileEndReason.Terrain)]
    [InlineData(true, ProjectileEndReason.Returned)]
    public void Return_can_ignore_walls_that_appeared_behind_the_outbound_flight(bool ignore, ProjectileEndReason end)
    {
        var world = new Arena(); var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Returns = true, IgnoreTerrainOnReturn = ignore })); runtime.AdvanceTo(124000);
        world.Terrain = (from, to) => from.X > 50 && to.X <= 50 ? new((50 - from.X) / (to.X - from.X), new(1, 0)) : null;
        runtime.AdvanceTo(244000);
        Assert.Equal(end, Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End).Reason);
    }

    [Theory]
    [InlineData(-1, 1, null)]
    [InlineData(101, 1, null)]
    [InlineData(0, 0, null)]
    [InlineData(0, double.NaN, null)]
    [InlineData(0, 1, -1)]
    public void Invalid_return_controls_are_rejected(int chance, double speed, int? hits)
    {
        var runtime = new ProjectileRuntime(new Arena());
        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.Release(Use(Basic with
            { ReturnChancePercent = chance, ReturnSpeedMultiplier = speed, ReturnHitLimit = hits })));
        Assert.Empty(runtime.Projectiles);
    }
}
