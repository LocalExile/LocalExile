using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class TownPortalExpiryTests
{
    [Fact]
    public void Only_a_successful_listing_that_omits_the_specific_instance_expires_the_route()
    {
        var listed = new ControlResponse { Ok = true, Instances = [new(41, 0, 60, []), new(42, 0, 30, [])] };
        Assert.False(PortalsFeature.OriginExpired(listed, 42));
        Assert.True(PortalsFeature.OriginExpired(listed, 43));
        Assert.True(PortalsFeature.OriginExpired(new() { Ok = true, Instances = [] }, 42));
        Assert.False(PortalsFeature.OriginExpired(new() { Ok = false, Instances = [] }, 42));
        Assert.False(PortalsFeature.OriginExpired(new() { Ok = true }, 42));
    }
}
