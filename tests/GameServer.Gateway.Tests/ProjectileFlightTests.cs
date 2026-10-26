using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ProjectileFlightTests
{
    [Fact]
    public void An_evaded_near_contact_does_not_hide_a_later_hit_in_the_same_swept_interval()
    {
        var targets = Observe(0, (9, Walker(80, 10)), (2, Walker(40, 10)));
        var attempted = new List<uint>();
        var step = Flight(targets).Advance(10, targets, id => { attempted.Add(id); return id == 9; });
        Assert.Equal(new uint[] { 2, 9 }, attempted);
        Assert.Equal(9u, step.TargetId);
        Assert.Equal(6.9, step.At, 6);
        Assert.Equal(79, step.Position.X, 6);
    }

    [Fact]
    public void An_evaded_overlapping_target_is_not_rolled_again_on_following_polls()
    {
        var targets = Observe(0, (1, Walker(40, 10)));
        var flight = Flight(targets);
        int attempts = 0;
        bool Miss(uint _) { attempts++; return false; }
        Assert.False(flight.Advance(3, targets, Miss).Finished);
        Assert.False(flight.Advance(3.05, targets, Miss).Finished);
        var end = flight.Advance(10, targets, Miss);
        Assert.Equal(1, attempts);
        Assert.Equal(0u, end.TargetId);
        Assert.Equal((110.0, 10.0), end.Position);
        Assert.Equal(end, flight.Advance(20, targets, _ => throw new Exception("Terminal flight cannot hit twice")));
    }

    [Fact]
    public void Missed_contacts_continue_to_the_wall_without_testing_targets_behind_it()
    {
        var targets = Observe(0, (1, Walker(30, 10)), (2, Walker(80, 10)));
        var flight = new ProjectileFlight((10, 10), (60, 10), 10, 0, 1, wall: true, targets);
        var attempted = new List<uint>();
        var end = flight.Advance(10, targets, id => { attempted.Add(id); return false; });
        Assert.Equal(new uint[] { 1 }, attempted);
        Assert.True(end.Wall);
        Assert.Equal(0u, end.TargetId);
        Assert.Equal((60.0, 10.0), end.Position);
    }

    [Fact]
    public void An_accepted_contact_still_stops_and_never_checks_more_distant_targets()
    {
        var targets = Observe(0, (3, Walker(80, 10)), (2, Walker(40, 10)), (1, Walker(40, 10)));
        var attempted = new List<uint>();
        var end = Flight(targets).Advance(10, targets, id => { attempted.Add(id); return true; });
        Assert.Equal(new uint[] { 1 }, attempted);
        Assert.Equal(1u, end.TargetId);
        Assert.Equal(2.9, end.At, 6);
    }

    [Fact]
    public void A_moving_crossing_can_be_evaded_before_a_later_contact_in_one_delayed_poll()
    {
        var crossing = Walker(40, 0, 10);
        crossing.Order(40, 30, 0);
        var far = Walker(80, 10);
        var initial = Observe(0, (1, crossing), (2, far));
        var flight = Flight(initial, speed: 30);
        var attempted = new List<uint>();
        var end = flight.Advance(3, Observe(3, (1, crossing), (2, far)), id => { attempted.Add(id); return id == 2; });
        // Observed motion over this interval crosses the projectile's sweep before the farther stationary enemy.
        Assert.Equal(new uint[] { 1, 2 }, attempted);
        Assert.Equal(2u, end.TargetId);
    }

    static WalkEstimator Walker(int x, int y, double speed = 10) => new(x, y, speed, width: 300, height: 300);
    static ProjectileFlight.Target[] Observe(double at, params (uint Id, WalkEstimator Walk)[] monsters) =>
        [.. monsters.Select(m => new ProjectileFlight.Target(m.Id, m.Walk.Position(at), m.Walk))];
    static ProjectileFlight Flight(ProjectileFlight.Target[] targets, double speed = 10) =>
        new((10, 10), (110, 10), speed, launched: 0, radius: 1, wall: false, targets);

    [Fact]
    public void A_monster_that_changes_course_before_contact_is_not_reserved_at_launch()
    {
        var monster = Walker(60, 10, 20);
        var flight = Flight(Observe(0, (1, monster)));
        Assert.False(flight.Advance(3, Observe(3, (1, monster))).Finished);
        monster.Order(60, 110, 3);
        ProjectileFlight.Step step = default;
        for (double at = 3.1; at < 10.2; at += 0.1) step = flight.Advance(at, Observe(at, (1, monster)));
        Assert.True(step.Finished);
        Assert.Equal(0u, step.TargetId);
        Assert.False(step.Wall);
        Assert.Equal((110.0, 10.0), step.Position);
    }

    [Fact]
    public void A_monster_that_walks_into_the_path_after_launch_is_hit()
    {
        var monster = Walker(60, 30);
        var flight = Flight(Observe(0, (1, monster)));
        Assert.False(flight.Advance(3, Observe(3, (1, monster))).Finished);
        monster.Order(60, 10, 3);
        ProjectileFlight.Step step = default;
        for (double at = 3.1; at < 5.2 && !step.Finished; at += 0.1)
            step = flight.Advance(at, Observe(at, (1, monster)));
        Assert.Equal(1u, step.TargetId);
        Assert.InRange(step.At, 4.8, 5.1);
        Assert.InRange(step.Position.X, 58, 61);
    }

    [Fact]
    public void A_later_nearer_interceptor_wins_over_the_original_far_target()
    {
        var far = Walker(90, 10);
        var near = Walker(50, 30);
        var flight = Flight(Observe(0, (9, far), (2, near)));
        flight.Advance(1, Observe(1, (9, far), (2, near)));
        near.Order(50, 10, 1);
        ProjectileFlight.Step step = default;
        for (double at = 1.1; at < 10.1 && !step.Finished; at += 0.1)
            step = flight.Advance(at, Observe(at, (9, far), (2, near)));
        Assert.Equal(2u, step.TargetId);
        Assert.InRange(step.Position.X, 48, 50);
    }

    [Fact]
    public void Relative_sweep_catches_a_crossing_even_when_neither_endpoint_overlaps()
    {
        var crossing = Walker(60, 0, 20);
        crossing.Order(60, 20, 0);
        var flight = Flight(Observe(0, (1, crossing)), speed: 100);
        var hit = flight.Advance(1, Observe(1, (1, crossing)));
        Assert.Equal(1u, hit.TargetId);
        Assert.InRange(hit.At, 0.48, 0.5);
        Assert.InRange(hit.Position.X, 58, 60);
    }

    [Fact]
    public void A_stale_or_dead_target_is_removed_from_the_observation_and_cannot_explode_the_projectile()
    {
        var monster = Walker(60, 10);
        var flight = Flight(Observe(0, (1, monster)));
        Assert.False(flight.Advance(2, Observe(2, (1, monster))).Finished);
        // Runtime filters removed/dead/zero-life monsters before advancing, with the monster lock held.
        var end = flight.Advance(10, []);
        Assert.True(end.Finished);
        Assert.Equal(0u, end.TargetId);
        Assert.False(end.Wall);
    }

    [Fact]
    public void A_newly_spawned_target_cannot_be_swept_backwards_through_time()
    {
        var flight = Flight([]);
        var behind = Walker(30, 10);
        var end = flight.Advance(5, Observe(5, (1, behind)));
        Assert.False(end.Finished);
        Assert.Equal(0u, end.TargetId);
    }

    [Fact]
    public void A_stopped_clock_preserves_flight_and_the_terminal_result_is_latched()
    {
        var monster = Walker(60, 10);
        var flight = Flight(Observe(0, (1, monster)));
        var before = flight.Advance(2, Observe(2, (1, monster)));
        for (int wallClockPoll = 0; wallClockPoll < 100; wallClockPoll++)
            Assert.Equal(before, flight.Advance(2, Observe(2, (1, monster))));
        var hit = flight.Advance(5, Observe(5, (1, monster)));
        Assert.Equal(1u, hit.TargetId);
        Assert.Equal(hit, flight.Advance(20, []));
    }

    [Fact]
    public void Earliest_contact_wins_independent_of_dictionary_order_and_ties_use_object_id()
    {
        var near = Walker(40, 10); var far = Walker(80, 10);
        var targets = Observe(0, (9, far), (3, near), (2, near));
        var hit = Flight(targets).Advance(10, targets);
        Assert.Equal(2u, hit.TargetId);
        Assert.Equal(2.9, hit.At, 6);
    }

    [Fact]
    public void Scheduler_delay_clips_monster_motion_to_the_projectiles_own_end_time()
    {
        var monster = Walker(110, 30, 10);
        var flight = Flight(Observe(0, (1, monster)));
        flight.Advance(9, Observe(9, (1, monster)));
        monster.Order(110, 0, 9);
        var step = flight.Advance(12, Observe(12, (1, monster)));
        Assert.True(step.Finished);
        Assert.Equal(0u, step.TargetId); // At t=10 the monster is still ten cells away; t=11 is too late.
    }

    [Fact]
    public void Replacing_a_monsters_walk_for_a_dev_teleport_does_not_create_a_crossing_trajectory()
    {
        var monster = Walker(43, 30);
        var flight = Flight(Observe(0, (1, monster)));
        monster = Walker(43, 0); // /mob_tp and /mob_move replace the estimator on an explicit placement.
        Assert.False(flight.Advance(5, Observe(5, (1, monster))).Finished);
    }

    [Fact]
    public void Linear_motion_contact_time_is_independent_of_polling_cadence()
    {
        var monster = Walker(60, 0, 20); monster.Order(60, 20, 0);
        var coarse = Flight(Observe(0, (1, monster)), 100);
        var fine = Flight(Observe(0, (1, monster)), 100);
        var first = coarse.Advance(1, Observe(1, (1, monster)));
        ProjectileFlight.Step second = default;
        for (int poll = 1; poll <= 10 && !second.Finished; poll++)
            second = fine.Advance(poll / 10.0, Observe(poll / 10.0, (1, monster)));
        Assert.Equal(first.TargetId, second.TargetId);
        Assert.Equal(first.At, second.At, 6);
    }

    [Fact]
    public void Transfer_cancellation_survives_ticket_consumption_and_failed_transfers_do_not_revive_old_flights()
    {
        var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);
        int before = conn.ProjectileEpoch;
        Assert.True(conn.ProjectileActive(before));
        conn.ProjectileObjects.UnionWith([0x3000u, 0x3001u]); // One flying object and one impact awaiting cleanup.
        Assert.True(conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out var remove));
        Assert.Equal(new[] { 0x3000u, 0x3001u }, remove.Order());
        Assert.Empty(conn.ProjectileObjects);
        Assert.False(conn.ProjectileActive(before));
        Assert.False(conn.TryBeginAreaChange(new PendingTransfer(3, 0, false), out _));
        conn.PendingTransfer = null; // Transfer consumes this before its asynchronous Auth call finishes.
        Assert.False(conn.ProjectileActive(conn.ProjectileEpoch));
        conn.ResumeAfterFailedTransfer();
        Assert.False(conn.ProjectileActive(before));
        Assert.True(conn.ProjectileActive(conn.ProjectileEpoch));
        conn.Closed = true;
        Assert.False(conn.ProjectileActive(conn.ProjectileEpoch));
    }

    [Fact]
    public void Paused_terminal_visuals_keep_their_cleanup_pending_until_resume_or_transfer()
    {
        var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);
        int epoch = conn.ProjectileEpoch;
        conn.ProjectileObjects.Add(0x3000);
        conn.Clock.Stop(); // Pause occurs after expiry was observed but before the packet reaches the send gate.
        Assert.False(conn.TryCommitProjectileVisual(epoch)); // Animated swap is deferred too.
        Assert.False(conn.TryCommitProjectileVisual(epoch, () => conn.ProjectileObjects.Remove(0x3000)));
        Assert.Contains(0x3000u, conn.ProjectileObjects); // Cleanup has not been lost by dropping a paused packet.
        conn.Clock.Start();
        Assert.True(conn.TryCommitProjectileVisual(epoch));
        Assert.True(conn.TryCommitProjectileVisual(epoch, () => conn.ProjectileObjects.Remove(0x3000)));
        Assert.Empty(conn.ProjectileObjects);

        conn.ProjectileObjects.Add(0x3001);
        conn.Clock.Stop();
        Assert.True(conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out var cleanup));
        Assert.Equal(new[] { 0x3001u }, cleanup); // The transfer takes ownership of paused cleanup.
        conn.Clock.Start();
        bool oldPacketCommitted = false;
        Assert.False(conn.TryCommitProjectileVisual(epoch, () => oldPacketCommitted = true));
        Assert.False(oldPacketCommitted);
    }

    [Fact]
    public void Wall_route_excludes_a_monster_behind_it_but_allows_a_nearer_monster()
    {
        var route = ProjectileFlight.ClipWall((10, 10), (110, 10), (x, _) => x == 50);
        Assert.True(route.Wall);
        Assert.Equal((49, 10), route.End);
        var behind = Walker(60, 10);
        var flight = new ProjectileFlight((10, 10), route.End, 10, 0, 1, route.Wall, Observe(0, (1, behind)));
        var end = flight.Advance(5, Observe(5, (1, behind)));
        Assert.True(end.Wall);
        Assert.Equal(0u, end.TargetId);
        Assert.Equal((49.0, 10.0), end.Position);
        var near = Walker(40, 10);
        flight = new((10, 10), route.End, 10, 0, 1, route.Wall, Observe(0, (2, near)));
        Assert.Equal(2u, flight.Advance(5, Observe(5, (2, near))).TargetId);
    }

    [Theory]
    [InlineData(10, 10, 110, 10)]
    [InlineData(110, 10, 10, 10)]
    [InlineData(10, 10, 10, 110)]
    [InlineData(10, 110, 10, 10)]
    public void Grid_trace_cannot_tunnel_through_a_single_cell_wall(int x0, int y0, int x1, int y1)
    {
        bool Blocked(int x, int y) => x == 50 || y == 50;
        var result = ProjectileFlight.ClipWall((x0, y0), (x1, y1), Blocked);
        Assert.True(result.Wall);
        Assert.False(Blocked(result.End.X, result.End.Y));
        Assert.False(ProjectileFlight.ClipWall((x0, y0), result.End, Blocked).Wall);
    }

    [Fact]
    public void Diagonal_corner_cells_and_a_wall_at_the_launch_cell_block()
    {
        Assert.Equal(((10, 10), true), ProjectileFlight.ClipWall((10, 10), (20, 20), (x, y) => x == 11 && y == 10));
        Assert.Equal(((10, 10), true), ProjectileFlight.ClipWall((10, 10), (20, 20), (_, _) => true));
        var flight = new ProjectileFlight((10, 10), (10, 10), 10, 0, 1, true, []);
        Assert.True(flight.Advance(0.1, []).Wall);
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
    public void Non_45_degree_exact_corners_check_both_side_cells_in_all_directions(
        int x0, int y0, int x1, int y1, int blockX, int blockY)
    {
        bool Blocks(int x, int y) => x == blockX && y == blockY;
        var clipped = ProjectileFlight.ClipWall((x0, y0), (x1, y1), Blocks);
        Assert.True(clipped.Wall);
        Assert.False(Blocks(clipped.End.X, clipped.End.Y));
        Assert.NotEqual((x1, y1), clipped.End);
        Assert.False(ProjectileFlight.ClipWall((x0, y0), clipped.End, Blocks).Wall);
    }

    [Fact]
    public void The_rounded_wire_endpoint_also_has_a_clear_route_through_adjacent_blockers()
    {
        var random = new Random(917);
        for (int map = 0; map < 200; map++)
        {
            var blocked = new HashSet<(int, int)>();
            for (int cell = 0; cell < 200; cell++) blocked.Add((random.Next(11, 90), random.Next(11, 90)));
            var end = (random.Next(40, 90), random.Next(40, 90));
            bool Blocks(int x, int y) => blocked.Contains((x, y));
            var clipped = ProjectileFlight.ClipWall((10, 10), end, Blocks);
            Assert.False(Blocks(clipped.End.X, clipped.End.Y));
            Assert.False(ProjectileFlight.ClipWall((10, 10), clipped.End, Blocks).Wall);
        }
    }
}
