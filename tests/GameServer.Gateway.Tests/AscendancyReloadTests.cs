namespace GameServer.Gateway.Tests;

public sealed class AscendancyReloadTests
{
    [Fact]
    public void Choosing_a_class_keeps_the_reward_chambers_instance_and_current_position()
    {
        var conn = new ClientState(new(123, "1_Labyrinth_boss_2_end", 2000, 2000, 10, 10, (_, _) => true, []), 1, 876)
        { InstanceNumber = 42 };
        conn.Walk.Teleport(1400, 1500, conn.Clock.Elapsed.TotalSeconds);
        var first = Game.AscendancyReload(conn);
        Assert.Equal(42ul, first.Instance);
        Assert.Equal(conn.Layout.Hash, first.Area);
        Assert.False(first.New);
        Assert.Equal((1400, 1500), first.ArriveAt);
        conn.Walk.Teleport(1700, 1600, conn.Clock.Elapsed.TotalSeconds);
        Assert.Equal((1700, 1600), Game.AscendancyReload(conn).ArriveAt);
        Assert.Equal((1400, 1500), first.ArriveAt);
    }
}
