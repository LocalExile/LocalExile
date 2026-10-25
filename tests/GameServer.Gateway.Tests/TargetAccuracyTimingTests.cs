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

    [Fact]
    public void The_player_entering_close_range_uses_its_current_position()
    {
        var conn = Character();
        conn.Walk.Order(130, 100, 0);
        var target = new SpawnedMonster("unique", new WalkEstimator(130, 100, 10), 0, 0, 100)
        { Rarity = MonsterRarity.Unique };
        Assert.Equal(140, Program.AttackAccuracyAt(conn, target, 0).Accuracy);
        Assert.Equal(210, Program.AttackAccuracyAt(conn, target, 1).Accuracy);
    }
}
