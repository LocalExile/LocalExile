using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class LivingCollisionTests
{
    [Fact]
    public void Fast_motion_stops_before_a_body_instead_of_tunnelling()
    {
        ClientPathfinder.Blocker[] bodies = [new(30, 20, 3)];
        var stop = LivingCollision.Sweep((10, 20), (70, 20), 1, (_, _) => true, bodies);
        Assert.Equal((25, 20), stop);
        Assert.Equal((70, 20), LivingCollision.Sweep((10, 20), (70, 20), 1, (_, _) => true, []));
    }

}
