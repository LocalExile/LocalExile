using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public class ProjectileSpreadTests
{
    [Theory]
    [InlineData(1, "0")]
    public void Runtime_places_a_centred_fan_within_the_profiles_total_spread(int count, string degrees)
    {
        var world = new ProjectileRuntimeProbe();
        world.Release(new("fan") { Count = count, SpreadRadians = Math.PI / 9 });
        Assert.Equal(degrees, string.Join(',', world.Runtime.Projectiles.Select(p =>
            Math.Round(Math.Atan2(p.Direction.Y, p.Direction.X) * 180 / Math.PI, 6))));
    }

}
