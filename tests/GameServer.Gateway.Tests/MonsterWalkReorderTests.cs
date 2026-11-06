using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class MonsterWalkReorderTests
{
    [Fact]
    public void Frequent_orders_on_a_diagonal_keep_the_walking_speed()
    {
        // Native Actor setup keeps a fixed percent without a draw; fixed does not mean zero.
        foreach (var (percent, expectedSpeed, expectedX) in new[] { (0, 10.0, 20), (30, 13.0, 26), (-20, 8.0, 16) })
        {
            foreach (uint key in new[] { 0u, 1u, uint.MaxValue })
                Assert.Equal(percent, MonsterSpeedRoll.Percent(key, ~key, percent, percent));
            double speed = 10 * MonsterSpeedRoll.Factor(MonsterSpeedRoll.Percent(123, 456, percent, percent));
            Assert.Equal(expectedSpeed, speed);
            var fixedWalk = new WalkEstimator(0, 0, speed);
            fixedWalk.Order(100, 0, 0);
            Assert.Equal((expectedX, 0), fixedWalk.Position(2));
        }
        var walk = new WalkEstimator(0, 0, speed: 8);
        const double gap = 0.125;
        for (int i = 0; i <= 40; i++) walk.Order(100, 100, i * gap);
        var (x, y) = walk.Position(40 * gap);
        Assert.InRange(Math.Sqrt(x * x + y * y), 39, 41);   // 8 cells/s for 5 s
    }
}
