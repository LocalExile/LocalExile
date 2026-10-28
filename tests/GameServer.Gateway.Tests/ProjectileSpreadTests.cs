using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public class ProjectileSpreadTests
{
    [Theory]
    [InlineData(1, "0")]
    [InlineData(2, "-10,10")]
    [InlineData(3, "-10,0,10")]
    [InlineData(5, "-10,-5,0,5,10")]
    public void Runtime_places_a_centred_fan_within_the_profiles_total_spread(int count, string degrees)
    {
        var world = new ProjectileRuntimeProbe();
        world.Release(new("fan") { Count = count, SpreadRadians = Math.PI / 9 });
        Assert.Equal(degrees, string.Join(',', world.Runtime.Projectiles.Select(p =>
            Math.Round(Math.Atan2(p.Direction.Y, p.Direction.X) * 180 / Math.PI, 6))));
    }

    [Theory]
    [InlineData(0, 10, 0, 1)]
    [InlineData(-10, 0, -1, 0)]
    [InlineData(0, -10, 0, -1)]
    public void Runtime_rotates_the_fan_with_the_aim(double x, double y, double dx, double dy)
    {
        var world = new ProjectileRuntimeProbe();
        world.Runtime.Release(new(new("fan"), new(1000), new(1000), 1, new(0, 0), new(x, y)));
        var direction = Assert.Single(world.Runtime.Projectiles).Direction;
        Assert.Equal(dx, direction.X, 6); Assert.Equal(dy, direction.Y, 6);
    }
}
