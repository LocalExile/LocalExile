using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class PlayerWalkTests
{
    static List<ClientPathfinder.Blocker> Ring(int cx, int cy) =>
        [.. Enumerable.Range(0, 16).Select(i => new ClientPathfinder.Blocker(
            cx + (int)Math.Round(5 * Math.Cos(i * Math.PI / 8)), cy + (int)Math.Round(5 * Math.Sin(i * Math.PI / 8)), 2))];

    [Fact]
    public void A_walker_enclosed_by_living_bodies_follows_the_click_out_around_the_stationary_blockers()
    {
        // The estimated bodies leave no gap: without a stationary fallback the walker stays inside the ring.
        var closed = new PlayerWalk(50, 50, speed: 37, blockers: () => Ring(50, 50));
        closed.Order(80, 50, now: 0);
        Assert.True(closed.Destination.X < 55);

        var w = new PlayerWalk(50, 50, speed: 37, blockers: () => Ring(50, 50), stationary: () => []);
        w.Order(80, 50, now: 0);
        Assert.Equal((80, 50), w.Destination);

        // Walls still hold: a target the walkable grid cannot reach keeps the walker where the fallback cannot go either.
        var walled = new PlayerWalk(50, 50, speed: 37, walkable: (x, _) => x < 60, blockers: () => Ring(50, 50), stationary: () => []);
        walled.Order(80, 50, now: 0);
        Assert.True(walled.Destination.X < 60);
    }

    [Fact]
    public void Clicking_an_occupied_destination_does_not_enable_crowd_escape()
    {
        var w = new PlayerWalk(50, 50, 37, blockers: () => [new(60, 50, 3)], stationary: () => []);
        w.Order(60, 50, 0);
        Assert.NotEqual((60, 50), w.Destination);
        Assert.False(w.EscapingCrowd(0));
    }

    [Fact]
    public void Crowd_escape_ends_on_arrival_teleport_or_replacement_order()
    {
        var w = new PlayerWalk(50, 50, 37, blockers: () => Ring(50, 50), stationary: () => []);
        w.Order(80, 50, 0);
        Assert.True(w.EscapingCrowd(0));
        Assert.False(w.EscapingCrowd(w.ArrivesAt));
        w.Teleport(50, 50, 1);
        Assert.False(w.EscapingCrowd(1));
        w.Order(80, 50, 1);
        Assert.True(w.EscapingCrowd(1));
        w.Order(50, 50, 1);
        Assert.False(w.EscapingCrowd(1));
    }

    [Fact]
    public void Steps_one_cell_per_step_time()
    {
        // 37 cells along x at 37 cells/s: one step every 1/37 s, whole cells only.
        var w = new PlayerWalk(0, 0, speed: 37);
        Assert.Equal((0, 0), w.Order(37, 0, now: 0));
        Assert.Equal((0, 0), w.Position(0.02));
        Assert.Equal((18, 0), w.Position(0.5));
        Assert.Equal((37, 0), w.Position(1.01));
        Assert.Equal(1.0, w.ArrivesAt, 3);
        Assert.Equal((37, 0), w.Destination);
    }

    [Fact]
    public void A_diagonal_takes_its_length_over_the_speed()
    {
        // (10, 10): 10 major-axis steps over √200 / 10 s.
        var w = new PlayerWalk(0, 0, speed: 10);
        w.Order(10, 10, 0);
        Assert.Equal(Math.Sqrt(200) / 10, w.ArrivesAt, 4);
        Assert.Equal((5, 5), w.Position(Math.Sqrt(200) / 20 + 0.001));
    }

    [Fact]
    public void A_new_order_keeps_the_step_in_progress()
    {
        // Half a step into the walk, a new order's first step comes half a step later.
        var w = new PlayerWalk(0, 0, speed: 10);
        w.Order(50, 0, 0);
        Assert.Equal((0, 0), w.Order(0, 50, 0.05));
        Assert.Equal((0, 0), w.Position(0.099));
        Assert.Equal((0, 1), w.Position(0.101));
    }

    [Fact]
    public void Approach_stops_within_reach()
    {
        var w = new PlayerWalk(0, 0, speed: 10);
        var (here, stop, at) = w.Approach(100, 0, reach: 8, now: 0);
        Assert.Equal((0, 0), here);
        Assert.Equal((92, 0), stop);
        Assert.Equal(9.2, at, 3);
    }

    [Fact]
    public void Approach_in_reach_stays()
    {
        var w = new PlayerWalk(5, 5, speed: 10);
        var (_, stop, at) = w.Approach(9, 5, reach: 8, now: 3);
        Assert.Equal((5, 5), stop);
        Assert.Equal(3, at, 3);
    }

    [Fact]
    public void Walks_around_an_npc()
    {
        var npc = new ClientPathfinder.Blocker(20, 0, 4);
        var w = new PlayerWalk(0, 0, 37, (x, y) => x >= 0 && y >= 0 && x < 60 && y < 60, 60, 60, () => [npc]);
        w.Order(40, 0, 0);
        Assert.Equal((40, 0), w.Destination);
        for (double t = 0; t < w.ArrivesAt; t += 0.005)
        {
            var (x, y) = w.Position(t);
            Assert.True(Math.Max(Math.Abs(x - 20), Math.Abs(y)) >= 6, $"({x}, {y}) at {t:0.000} is inside the NPC");
        }
    }

    [Fact]
    public void Teleport_stops_the_walk()
    {
        var w = new PlayerWalk(0, 0, speed: 10);
        w.Order(50, 0, 0);
        w.Teleport(7, 7, 1);
        Assert.Equal((7, 7), w.Position(2));
        Assert.Equal((7, 7), w.Destination);
    }

    [Fact]
    public void Walk_up_to_an_object_stops_in_its_range()
    {
        // The stash (size 2) at (40, 0): max range 5, tolerance 2 → the walker stops 7 cells short.
        var w = new PlayerWalk(0, 0, speed: 37);
        var (_, stop, _) = w.WalkUpTo(40, 0, 2, now: 0);
        Assert.Equal((33, 0), stop);
        Assert.True(PlayerWalk.InWalkUpRange(stop, (40, 0), 2));
        Assert.False(PlayerWalk.InWalkUpRange((32, 0), (40, 0), 2));
    }
}
