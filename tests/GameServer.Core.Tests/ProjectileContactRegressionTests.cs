using GameServer.Core.Projectiles;

namespace GameServer.Core.Tests;

public class ProjectileContactRegressionTests
{
    sealed class Arena : IProjectileWorld
    {
        public readonly List<ProjectileActor> Targets = [new(new(1), 1, _ => new(0, 0), 0)];
        public readonly List<ProjectileDamage> Hits = [];
        public readonly List<ProjectileEvent> Events = [];
        public Func<ProjectileDamage, ProjectileHitResult> Accept = _ => ProjectileHitResult.Hit;
        public Func<ProjectileVector, ProjectileVector, TerrainContact?> Terrain = (_, _) => null;
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => Targets;
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => Terrain(from, to);
        public ProjectileHitResult Hit(ProjectileDamage damage)
        {
            var result = Accept(damage);
            if (result == ProjectileHitResult.Hit) Hits.Add(damage);
            return result;
        }
        public void Publish(ProjectileEvent value) => Events.Add(value);
        public void Target(uint id, double x, double y = 0) => Targets.Add(new(new(id), 2, _ => new(x, y), 0));
    }
    static ProjectileProfile Basic => new("synthetic") { Speed = 100, Range = 100, Radius = 1 };
    static ProjectileUse Use(ProjectileProfile profile) => new(profile, new(1), new(1), 1, new(0, 0), new(100, 0))
        { ReturnAnchor = new(1) };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Returning_chain_and_split_ignore_the_turnaround_actor_and_use_valid_alternatives(bool split, bool alternative)
    {
        var world = new Arena(); world.Target(2, 30);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with
        {
            Returns = true, ExcludeTurnaroundTarget = true, ReturnOvershoot = 150,
            Chain = split ? 0 : 1, Split = split ? 1 : 0,
            EndArea = new("terminal", 2)
        }));
        runtime.AdvanceTo(36000);
        Assert.Equal(1, Assert.Single(runtime.Projectiles).Phase);
        world.Target(3, 10);
        if (alternative) world.Target(4, 45, 20);
        runtime.AdvanceTo(120000);
        Assert.Single(world.Hits, h => h.Target.Id == 2); // The excluded actor receives only its outbound hit.
        var newlyEncountered = Assert.Single(world.Hits, h => h.Target.Id == 3);
        Assert.Equal(1, newlyEncountered.Phase);
        Assert.Equal(!alternative, newlyEncountered.IsArea);
        var terminal = Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Area);
        Assert.InRange(terminal.Position.X, alternative ? 44 : 10.999, alternative ? 46 : 11.001);
        if (alternative)
            Assert.True(Assert.Single(world.Hits, h => h.Target.Id == 4).IsArea);
        else
        {
            Assert.Equal("terminal", newlyEncountered.Stage);
            Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Redirect); // Only the turnaround.
            Assert.DoesNotContain(world.Events, e => e.Kind == ProjectileEventKind.Spawn && e.Projectile.ParentId != 0);
        }
        Assert.Empty(runtime.Projectiles);
    }

    [Fact]
    public void Return_candidate_exclusion_preserves_shared_quota_in_the_new_phase()
    {
        var world = new Arena(); world.Target(2, 30);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with
        {
            Count = 2, SpreadRadians = 0, Returns = true, ExcludeTurnaroundTarget = true,
            SkipDeniedContacts = true, SkipDeniedReturnContacts = true, PierceAllOnReturn = true, Chain = 1
        }));
        runtime.AdvanceTo(36000); world.Target(3, 10);
        runtime.AdvanceTo(360000);
        Assert.Single(world.Hits, h => h.Target.Id == 2 && h.Phase == 0);
        Assert.Single(world.Hits, h => h.Target.Id == 2 && h.Phase == 1);
        Assert.Single(world.Hits, h => h.Target.Id == 3 && h.Phase == 1);
    }

    [Fact]
    public void Wall_turnaround_starts_on_the_incident_side_and_defers_terminal_area()
    {
        var world = new Arena { Terrain = (from, to) => ProjectileGeometry.TraceGrid(from, to, (x, _) => x >= 50) };
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { DirectDamage = false, Returns = true, EndArea = new("terminal", 5) }));
        runtime.AdvanceTo(72000);
        Assert.Equal(1, Assert.Single(runtime.Projectiles).Phase);
        Assert.DoesNotContain(world.Events, e => e.Kind == ProjectileEventKind.Area);
        runtime.AdvanceTo(240000);
        Assert.Equal(0, Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Area).Position.X, 4);
    }

    [Fact]
    public void Sector_areas_keep_inner_spacing_and_skip_blocked_wall_sectors()
    {
        var world = new Arena
        {
            Terrain = (from, to) => from.X <= 50 && to.X >= 50 && to.X > from.X
                ? new((50 - from.X) / (to.X - from.X), new(-1, 0)) : null
        };
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { DirectDamage = false, EndArea = new("offset", 14, 4, 20)
            { MinimumOffset = 18, SectorPlacement = true, RespectTerrain = true } }));
        runtime.AdvanceTo(120000);
        var areas = world.Events.Where(e => e.Kind == ProjectileEventKind.Area).ToArray();
        Assert.InRange(areas.Length, 1, 3);
        Assert.All(areas, e =>
        {
            Assert.True(e.Position.X < 50);
            Assert.InRange((e.Position - new ProjectileVector(50, 0)).Length, 18, 20);
        });
    }

    [Fact]
    public void Evading_terminal_victim_keeps_flying_until_an_admitted_terminal_contact()
    {
        var world = new Arena(); world.Target(2, 20); world.Target(3, 60);
        var attempts = new List<uint>();
        world.Accept = damage => { attempts.Add(damage.Target.Id); return damage.Target.Id == 2
            ? ProjectileHitResult.Miss : ProjectileHitResult.Hit; };
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { EndArea = new("blast", 5) }));
        runtime.AdvanceTo(120000);
        Assert.Equal(new uint[] { 2, 3 }, attempts);
        Assert.Equal(3u, Assert.Single(world.Hits).Target.Id);
        Assert.Equal(59, Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Area).Position.X, 4);
    }

    [Fact]
    public void Return_defers_terminal_area_until_final_phase_and_keeps_contact_area()
    {
        var world = new Arena(); world.Target(2, 30);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Returns = true, ExcludeTurnaroundTarget = true,
            ContactArea = new("contact", 2), EndArea = new("terminal", 2) }));
        runtime.AdvanceTo(40000);
        Assert.Equal("direct", world.Hits[0].Stage);
        Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Area);
        Assert.DoesNotContain(world.Hits, h => h.Stage == "terminal");
        runtime.AdvanceTo(100000);
        Assert.Equal(2, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
        Assert.Empty(runtime.Projectiles);
    }

    [Fact]
    public void Split_children_created_during_return_keep_the_phase_and_cannot_return_again()
    {
        var world = new Arena(); world.Target(2, 20);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Returns = true, ExcludeTurnaroundTarget = true, Split = 1, ChainRange = 5 }));
        runtime.Schedule(27000, () => { world.Target(3, 12); world.Target(4, 10); });
        runtime.AdvanceTo(120000);
        Assert.Equal(new uint[] { 2, 3, 4 }, world.Hits.Select(h => h.Target.Id));
        Assert.Equal(new[] { 0, 1, 1 }, world.Hits.Select(h => h.Phase));
        Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Redirect);
        Assert.Contains(world.Events, e => e.Kind == ProjectileEventKind.Spawn && e.Projectile.ParentId != 0);
        Assert.Empty(runtime.Projectiles);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void Return_pierce_all_is_explicit_and_cannot_pierce_still_takes_precedence(bool prohibited, int hits)
    {
        var world = new Arena(); world.Target(2, 30);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Returns = true, ExcludeTurnaroundTarget = true,
            PierceAllOnReturn = true, CannotPierce = prohibited }));
        runtime.Schedule(40000, () => { world.Target(3, 10); world.Target(4, 5); });
        runtime.AdvanceTo(120000);
        Assert.Equal(hits, world.Hits.Count(h => h.Phase == 1));
    }

    [Fact]
    public void Return_cannot_refill_lifetime_or_revive_an_expired_flight()
    {
        var world = new Arena();
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Returns = true, Lifetime = 30000 }));
        runtime.AdvanceTo(120000);
        Assert.DoesNotContain(world.Events, e => e.Kind == ProjectileEventKind.Redirect);
        Assert.Equal(30000, Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End).At);
        world.Events.Clear(); world.Target(2, 20);
        runtime.Release(Use(Basic with { Returns = true, Lifetime = 30000, ExcludeTurnaroundTarget = true }));
        runtime.AdvanceTo(240000);
        Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Redirect);
        Assert.Equal(150000, Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End).At);
    }

    [Fact]
    public void Return_uses_remaining_continuations_and_ends_after_the_next_unpierced_victim()
    {
        var world = new Arena(); world.Target(2, 30);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { Returns = true, ExcludeTurnaroundTarget = true }));
        runtime.Schedule(40000, () => { world.Target(3, 10); world.Target(4, 5); });
        runtime.AdvanceTo(120000);
        Assert.Equal(new uint[] { 2, 3 }, world.Hits.Select(h => h.Target.Id));
        Assert.Empty(runtime.Projectiles);
    }

    [Fact]
    public void Each_contact_explosion_can_overlap_the_primary_independently_of_direct_admission()
    {
        var world = new Arena(); world.Target(2, 30);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(Use(Basic with { ContactArea = new("cluster", 5, Count: 4) }));
        runtime.AdvanceTo(120000);
        Assert.Single(world.Hits, h => h.Stage == "direct");
        Assert.Equal(4, world.Hits.Count(h => h.Stage == "cluster"));
    }

    [Fact]
    public void Finite_pierce_hands_off_to_chain_and_prohibition_skips_to_chain()
    {
        foreach (bool prohibited in new[] { false, true })
        {
            var world = new Arena(); world.Target(2, 20); world.Target(3, 40); world.Target(4, 40, 20);
            var runtime = new ProjectileRuntime(world);
            runtime.Release(Use(Basic with { Pierce = 1, Chain = 1, CannotPierce = prohibited }));
            runtime.AdvanceTo(120000);
            Assert.Equal(prohibited ? new uint[] { 2, 3 } : [2u, 3u, 4u], world.Hits.Select(h => h.Target.Id));
            Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Redirect);
            Assert.Empty(runtime.Projectiles);
        }
    }
}
