using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class MonsterWalkReorderTests
{
    [Fact]
    public void Frequent_orders_on_a_diagonal_keep_the_walking_speed()
    {
        var walk = new WalkEstimator(0, 0, speed: 8);
        const double gap = 0.125;
        for (int i = 0; i <= 40; i++) walk.Order(100, 100, i * gap);
        var (x, y) = walk.Position(40 * gap);
        Assert.InRange(Math.Sqrt(x * x + y * y), 39, 41);   // 8 cells/s for 5 s
    }
}
