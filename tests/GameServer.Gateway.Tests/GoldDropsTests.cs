using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class GoldDropsTests
{
    static List<int> Rolls(int level, MonsterRarity rarity) =>
        [.. Enumerable.Range(0, 2000).Select(i => ItemDrops.GoldAmount(level, rarity, new Random(i)))];

    [Fact]
    public void Normal_monsters_drop_gold_now_and_then_rares_always()
    {
        var normal = Rolls(10, MonsterRarity.Normal);
        Assert.InRange(normal.Count(g => g > 0), 700, 900);   // 40%
        Assert.All(Rolls(10, MonsterRarity.Rare), g => Assert.True(g > 0));
    }

}
