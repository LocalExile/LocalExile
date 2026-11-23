using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class AtlasKillRewardTests
{
    [Fact]
    public void A_map_tier_selector_runs_once_after_the_reward_roll_succeeds()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var drops = Game.DropsFor(f.Player);
        int calls = 0;
        int Upgrade(int tier)
        {
            Assert.Equal(12, tier);
            calls++;
            return tier + 1;
        }

        Assert.Null(drops.Map(79, MonsterRarity.Rare, 12, new FixedRoll(.99), Upgrade));
        Assert.Equal(0, calls);
        var reward = drops.Map(79, MonsterRarity.Rare, 12, new FixedRoll(0), Upgrade);
        Assert.NotNull(reward);
        Assert.Equal("Metadata/Items/Maps/MapKeyTier13", reward.Type.Id);
        Assert.Equal(1, calls);
    }

    sealed class FixedRoll(double roll) : Random(467)
    {
        public override double NextDouble() => roll;
    }
}
