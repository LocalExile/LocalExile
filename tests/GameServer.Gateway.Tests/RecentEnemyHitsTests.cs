namespace GameServer.Gateway.Tests;

public class RecentEnemyHitsTests
{
    [Fact]
    public void Real_node_stat_keys_and_four_second_reminder_are_required_inputs()
    {
        Assert.Equal(20680u, Program.Stats.Key(RecentEnemyHits.PerHit));
        Assert.Equal(4534u, Program.Stats.Key(RecentEnemyHits.Counter));
        Assert.Equal(8404u, Program.Stats.Key(RecentEnemyHits.GenericChance));
        Assert.Equal(12268u, Program.Stats.Key(RecentEnemyHits.VirtualChance));
        var node = new PassiveTree().Find(2103);
        Assert.NotNull(node);
        Assert.Equal("Steel Willed", node.Name);
        Assert.Contains((20680u, 10), node.Stats);
        Assert.Equal(4, RecentEnemyHits.RecentlySeconds);
    }

    sealed class ArmourRoll(int roll) : Random
    {
        public int Calls;
        public override int Next(int maxValue) { Calls++; Assert.Equal(100, maxValue); return roll; }
    }
}
