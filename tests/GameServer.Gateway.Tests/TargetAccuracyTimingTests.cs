using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class TargetAccuracyTimingTests
{
    static ClientState Character() => new(new(0, "AccuracyTest", 256, 256, 100, 100, null, []), 0, 1)
    {
        Pools = new(1, 100, 100, 100, 0, AccuracyMoreVsUniquePct: 40, AccuracyMoreInCloseRangePct: 50)
    };

    [Fact]
    public void A_target_walking_out_of_close_range_is_checked_at_contact_not_attack_start()
    {
        var conn = Character();
        var target = new SpawnedMonster("moving", new WalkEstimator(120, 100, 10), 0, 0, 100);
        target.Walk.Order(140, 100, 0);
        Assert.Equal(150, Program.AttackAccuracyAt(conn, target, 0).Accuracy);
        Assert.Equal(100, Program.AttackAccuracyAt(conn, target, 1).Accuracy);
    }

}
