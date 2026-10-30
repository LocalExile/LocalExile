using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ProjectileHeightTests
{
    [Theory]
    [InlineData(null, null, 5)]
    public void Flight_height_preserves_main_ground_fallback_and_configured_offset(int? ground, string? setting, int expected)
    {
        Assert.Equal(expected, ProjectileHeight.Flight(ground, setting).Marker);
    }

}
