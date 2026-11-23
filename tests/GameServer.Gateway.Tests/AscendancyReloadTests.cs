namespace GameServer.Gateway.Tests;

public sealed class AscendancyReloadTests
{
    [Fact]
    public void Choosing_a_class_keeps_the_reward_chambers_instance_and_current_position()
    {
        var player = new Player(new(123, "1_Labyrinth_boss_2_end", 2000, 2000, 10, 10, (_, _) => true, []), 1, 876)
        { InstanceNumber = 42 };
        player.Walk.Teleport(1400, 1500, player.Clock.Elapsed.TotalSeconds);
        var first = Game.AscendancyReload(player);
        Assert.Equal(42ul, first.Instance);
        Assert.Equal(player.Layout.Hash, first.Area);
        Assert.False(first.New);
        Assert.Equal((1400, 1500), first.ArriveAt);
        player.Walk.Teleport(1700, 1600, player.Clock.Elapsed.TotalSeconds);
        Assert.Equal((1700, 1600), Game.AscendancyReload(player).ArriveAt);
        Assert.Equal((1400, 1500), first.ArriveAt);
    }
}
