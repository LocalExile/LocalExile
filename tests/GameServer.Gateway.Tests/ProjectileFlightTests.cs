using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public class ProjectileFlightTests
{
    static ProjectileProfile Arrow => new("test") { Speed = 10, Range = 100, Radius = 1, Lifetime = ProjectileRuntime.Time(20) };

    [Fact]
    public void An_evaded_near_contact_does_not_hide_a_later_hit_in_the_same_delayed_advance()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(9, 80, 10); world.Target(2, 40, 10);
        world.Accept = hit => hit.Target.Id == 9 ? ProjectileHitResult.Hit : ProjectileHitResult.Miss;
        world.Release(); world.Advance(10.01);
        Assert.Equal(new uint[] { 2, 9 }, world.Attempts.Select(h => h.Target.Id));
        Assert.InRange(world.Attempts[1].At, ProjectileRuntime.Time(6.9) - 1, ProjectileRuntime.Time(6.9) + 1);
        Assert.InRange(world.Attempts[1].Position.X, 79, 79 + 10d / ProjectileRuntime.QuantaPerSecond + 1e-9);
        Assert.Empty(world.Runtime.Projectiles);
    }

    [Fact]
    public void An_evaded_overlapping_target_is_not_rolled_again_on_following_polls()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 40, 10);
        world.Accept = _ => ProjectileHitResult.Miss; world.Release();
        world.Advance(3); world.Advance(3.05); world.Advance(10.01); world.Advance(20);
        Assert.Single(world.Attempts);
        var end = Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End);
        Assert.Equal(ProjectileEndReason.Distance, end.Reason);
        Assert.Equal(110, end.Position.X, 9); Assert.Equal(10, end.Position.Y, 9);
    }

    [Fact]
    public void Missed_contacts_continue_to_the_wall_without_testing_targets_behind_it()
    {
        var world = new ProjectileRuntimeProbe { Blocked = (x, _) => x == 60 };
        world.Target(1, 30, 10); world.Target(2, 80, 10);
        world.Accept = _ => ProjectileHitResult.Miss; world.Release(); world.Advance(10.01);
        Assert.Equal(1u, Assert.Single(world.Attempts).Target.Id);
        var end = Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End);
        Assert.Equal(ProjectileEndReason.Terrain, end.Reason);
        Assert.Equal(59.5, end.Position.X, 5);
    }

    [Fact]
    public void Earliest_contact_wins_independent_of_dictionary_order_and_ties_use_object_id()
    {
        var world = new ProjectileRuntimeProbe();
        world.Target(9, 80, 10); world.Target(3, 40, 10); world.Target(2, 40, 10);
        world.Release(); world.Advance(10.01);
        var hit = Assert.Single(world.Attempts);
        Assert.Equal(2u, hit.Target.Id); Assert.InRange(hit.At, ProjectileRuntime.Time(2.9) - 1, ProjectileRuntime.Time(2.9) + 1);
    }

    [Fact]
    public void A_moving_crossing_can_be_evaded_before_a_later_contact()
    {
        var world = new ProjectileRuntimeProbe();
        world.Target(1, at => new(40, Math.Min(30, at * 10)));
        world.Target(2, 80, 10);
        world.Accept = hit => hit.Target.Id == 2 ? ProjectileHitResult.Hit : ProjectileHitResult.Miss;
        world.Release(Arrow with { Speed = 30 }); world.Advance(3);
        Assert.Equal(new uint[] { 1, 2 }, world.Attempts.Select(h => h.Target.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_target_that_changes_course_before_contact_is_not_reserved_at_launch(bool piercing)
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 60, 10);
        world.Release(Arrow with { Pierce = piercing ? 2 : 0 }); world.Advance(3);
        world.Targets.Clear(); world.Target(1, at => new(60, 10 + Math.Min(100, (at - 3) * 20)));
        world.Advance(10.01);
        Assert.Empty(world.Attempts); Assert.Empty(world.Runtime.Projectiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_target_that_walks_into_the_route_after_launch_is_hit(bool piercing)
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 60, 30);
        world.Release(Arrow with { Pierce = piercing ? 2 : 0 }); world.Advance(3);
        world.Targets.Clear(); world.Target(1, at => new(60, Math.Max(10, 30 - (at - 3) * 20)));
        world.Advance(10.01);
        var hit = Assert.Single(world.Attempts);
        Assert.Equal(1u, hit.Target.Id);
        Assert.InRange(hit.At, ProjectileRuntime.Time(4.8), ProjectileRuntime.Time(5.2));
    }

    [Fact]
    public void A_later_nearer_interceptor_wins_over_the_original_far_target()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(9, 90, 10); world.Target(2, 50, 30);
        world.Release(); world.Advance(1);
        world.Targets.RemoveAll(t => t.Key.Id == 2);
        world.Target(2, at => new(50, Math.Max(10, 30 - (at - 1) * 10)));
        world.Advance(10.01);
        Assert.Equal(2u, Assert.Single(world.Attempts).Target.Id);
    }

    [Fact]
    public void Relative_sweep_catches_a_crossing_and_is_independent_of_polling_cadence()
    {
        ProjectileRuntimeProbe Run(bool fine)
        {
            var world = new ProjectileRuntimeProbe();
            world.Target(1, at => new(60, Math.Min(20, at * 20)));
            world.Release(Arrow with { Speed = 100 });
            if (fine) for (int i = 1; i <= 10; i++) world.Advance(i / 10d);
            else world.Advance(1);
            return world;
        }
        var coarse = Assert.Single(Run(false).Attempts); var fine = Assert.Single(Run(true).Attempts);
        Assert.Equal(coarse.At, fine.At); Assert.Equal(coarse.Target, fine.Target);
        Assert.InRange(coarse.At, ProjectileRuntime.Time(.48), ProjectileRuntime.Time(.5));
    }

    [Fact]
    public void A_piercing_sweep_reports_every_contact_once_in_order_and_keeps_flying()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(9, 80, 10); world.Target(2, 40, 10);
        world.Release(Arrow with { Pierce = 2 }); world.Advance(3); world.Advance(10.01);
        Assert.Equal(new uint[] { 2, 9 }, world.Attempts.Select(h => h.Target.Id));
        Assert.InRange(world.Attempts[0].At, ProjectileRuntime.Time(2.9) - 1, ProjectileRuntime.Time(2.9) + 1);
        Assert.InRange(world.Attempts[1].At, ProjectileRuntime.Time(6.9) - 1, ProjectileRuntime.Time(6.9) + 1);
        Assert.Equal(ProjectileEndReason.Distance, Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End).Reason);
    }

    [Fact]
    public void Removed_targets_cannot_explode_the_projectile_and_new_targets_cannot_be_swept_backwards()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 60, 10); world.Release(); world.Advance(2);
        world.Targets.Clear(); world.Advance(5); world.Target(2, 30, 10); world.Advance(10.01);
        Assert.Empty(world.Attempts); Assert.Empty(world.Runtime.Projectiles);
    }

    [Fact]
    public void A_stopped_horizon_and_a_terminal_result_cannot_apply_a_hit_twice()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 60, 10); world.Release(); world.Advance(2);
        var before = Assert.Single(world.Runtime.Projectiles).Position;
        for (int i = 0; i < 100; i++) world.Advance(2);
        Assert.Equal(before, Assert.Single(world.Runtime.Projectiles).Position);
        world.Advance(5); world.Advance(20);
        Assert.Single(world.Attempts); Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End);
    }

    [Fact]
    public void Scheduler_delay_clips_monster_motion_to_the_projectiles_own_end_time()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 110, 30); world.Release(); world.Advance(9);
        world.Targets.Clear(); world.Target(1, at => new(110, Math.Max(0, 30 - (at - 9) * 10)));
        world.Advance(12); Assert.Empty(world.Attempts); Assert.Empty(world.Runtime.Projectiles);
    }

    [Fact]
    public void Replacing_an_actor_for_a_dev_teleport_does_not_create_a_crossing_trajectory()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 43, 30); world.Release(); world.Advance(1);
        world.Targets.Clear(); world.Target(1, 43, 0); world.Advance(5); Assert.Empty(world.Attempts);
    }

    [Fact]
    public void Transfer_cancellation_survives_ticket_consumption_and_failed_transfer()
    {
        var conn = new ClientState(new(1, "test", 200, 200, 10, 10, null, []), 1, 1);
        int before = conn.ProjectileEpoch; conn.ProjectileObjects.UnionWith([0x3000u, 0x3001u]);
        Assert.True(conn.TryBeginAreaChange(new(2, 0, false), out var remove));
        Assert.Equal(new[] { 0x3000u, 0x3001u }, remove.Order()); Assert.Empty(conn.ProjectileObjects);
        Assert.False(conn.ProjectileActive(before)); Assert.False(conn.TryBeginAreaChange(new(3, 0, false), out _));
        conn.PendingTransfer = null; Assert.False(conn.ProjectileActive(conn.ProjectileEpoch));
        conn.ResumeAfterFailedTransfer(); Assert.False(conn.ProjectileActive(before));
        Assert.True(conn.ProjectileActive(conn.ProjectileEpoch)); conn.Closed = true;
        Assert.False(conn.ProjectileActive(conn.ProjectileEpoch));
    }

    [Fact]
    public async Task Paused_terminal_output_keeps_cleanup_pending_until_resume_or_transfer()
    {
        foreach (bool transfer in new[] { false, true })
        {
            await using var f = new ProjectileRuntimeFixture();
            f.Release(); await f.Read(); uint id = Assert.Single(f.Conn.ProjectileObjects);
            await f.HoldOutput(); f.Advance(10.01);
            Assert.Empty(f.Session.Runtime.Projectiles); // Terminal simulation precedes paused output cleanup.
            f.Conn.Clock.Stop(); f.ReleaseOutput();
            Assert.Contains(id, f.Conn.ProjectileObjects);
            if (transfer)
            {
                Assert.True(f.Conn.TryBeginAreaChange(new(2, 0, false), out var cleanup));
                Assert.Contains(id, cleanup); Assert.Empty(f.Conn.ProjectileObjects);
                await f.Session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                f.Conn.Clock.Start(); await f.Read(); Assert.Empty(f.Conn.ProjectileObjects);
            }
        }
    }

    [Theory]
    [InlineData(10, 10, 110, 10)]
    [InlineData(110, 10, 10, 10)]
    [InlineData(10, 10, 10, 110)]
    [InlineData(10, 110, 10, 10)]
    public void Grid_trace_cannot_tunnel_through_a_single_cell_wall(int x0, int y0, int x1, int y1)
    {
        Assert.NotNull(ProjectileGeometry.TraceGrid(new(x0, y0), new(x1, y1), (x, y) => x == 50 || y == 50));
    }

    [Fact]
    public void Diagonal_corner_side_cells_and_a_wall_at_launch_block()
    {
        Assert.Equal(.05, ProjectileGeometry.TraceGrid(new(10, 10), new(20, 20), (x, y) => x == 11 && y == 10)!.Value.Fraction, 6);
        Assert.Equal(0, ProjectileGeometry.TraceGrid(new(10, 10), new(20, 20), (_, _) => true)!.Value.Fraction);
        var world = new ProjectileRuntimeProbe { Blocked = (_, _) => true }; world.Release(); world.Advance(.1);
        Assert.Equal(ProjectileEndReason.Terrain, Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End).Reason);
    }

    [Theory]
    [InlineData(10, 10, 24, 16, 13, 12)]
    [InlineData(24, 16, 10, 10, 13, 12)]
    [InlineData(40, 10, 26, 16, 37, 12)]
    [InlineData(26, 16, 40, 10, 37, 12)]
    [InlineData(10, 40, 24, 34, 13, 38)]
    [InlineData(24, 34, 10, 40, 13, 38)]
    [InlineData(40, 40, 26, 34, 37, 38)]
    [InlineData(26, 34, 40, 40, 37, 38)]
    [InlineData(10, 10, 16, 24, 12, 13)]
    [InlineData(16, 24, 10, 10, 12, 13)]
    public void Non_45_degree_corner_side_cells_block_in_all_directions(int x0, int y0, int x1, int y1, int bx, int by)
    {
        var hit = ProjectileGeometry.TraceGrid(new(x0, y0), new(x1, y1), (x, y) => x == bx && y == by);
        Assert.NotNull(hit); Assert.InRange(hit.Value.Fraction, 0, .999999);
    }

    [Fact]
    public void A_wall_blocks_the_far_monster_but_allows_a_nearer_contact()
    {
        foreach (bool near in new[] { false, true })
        {
            var world = new ProjectileRuntimeProbe { Blocked = (x, _) => x == 50 };
            world.Target(1, near ? 40 : 60, 10); world.Release(); world.Advance(10.01);
            Assert.Equal(near ? 1 : 0, world.Attempts.Count);
            Assert.Equal(near ? ProjectileEndReason.Enemy : ProjectileEndReason.Terrain,
                Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End).Reason);
        }
    }
}
