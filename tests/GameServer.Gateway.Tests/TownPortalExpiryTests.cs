using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class TownPortalExpiryTests
{
    [Fact]
    public void Only_a_successful_listing_that_omits_the_specific_instance_expires_the_route()
    {
        var listed = new ControlResponse { Ok = true, Instances = [new(41, 0, 60, []), new(42, 0, 30, [])] };
        Assert.False(AreaInstances.Gone(listed, 42));
        Assert.True(AreaInstances.Gone(listed, 43));
        Assert.True(AreaInstances.Gone(new() { Ok = true, Instances = [] }, 42));
        Assert.False(AreaInstances.Gone(new() { Ok = false, Instances = [] }, 42));
        Assert.False(AreaInstances.Gone(new() { Ok = true }, 42));

        var conn = SpellTotemExecutionTests.PlayerOf("TownPortalArrival");
        var town = AreaLayouts.For(AreaTransitionCatalog.HashOf("1_2_town")!.Value, 1);
        conn.Use(town);
        var portal = PortalsFeature.TownPortalPosition(conn);
        conn.Use(town with { SpawnX = town.SpawnX + 60, SpawnY = town.SpawnY + 40 });
        Assert.Equal(portal, PortalsFeature.TownPortalPosition(conn));
    }
}
