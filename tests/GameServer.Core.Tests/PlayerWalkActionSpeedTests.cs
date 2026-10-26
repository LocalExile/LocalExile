using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class PlayerWalkActionSpeedTests
{
    [Fact]
    public void Zero_speed_stops_an_existing_order_and_restoring_speed_resumes_from_the_stopped_cell()
    {
        var walk = new PlayerWalk(0, 0, 10);
        walk.Order(100, 0, 0);
        walk.SetSpeed(0, 2.01);
        Assert.Equal((20, 0), walk.Position(100));
        Assert.Equal(0, walk.Speed);
        walk.SetSpeed(10, 100);
        Assert.Equal((60, 0), walk.Position(104.01));
        Assert.Equal((100, 0), walk.Position(108.01));
    }

    [Fact]
    public void Orders_at_zero_speed_stay_finite_and_teleport_clears_the_suspended_order()
    {
        var walk = new PlayerWalk(0, 0, 0);
        walk.Order(50, 0, 0);
        Assert.Equal((0, 0), walk.Position(10));
        Assert.True(double.IsFinite(walk.ArrivesAt));
        walk.SetSpeed(10, 10);
        Assert.Equal((10, 0), walk.Position(11.01));
        walk.SetSpeed(0, 11.01);
        walk.Teleport(5, 5, 12);
        walk.SetSpeed(10, 13);
        Assert.Equal((5, 5), walk.Position(100));
    }
}
