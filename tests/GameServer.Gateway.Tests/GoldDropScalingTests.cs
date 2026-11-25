using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class GoldDropScalingTests
{
    [Fact]
    public void Gold_level_curve_and_rarity_scaling_match_retail_bounds()
    {
        // Level curve is strictly monotonic from L1 to L100 without exponential blowup above L68.
        for (int level = 1; level < 100; level++)
            Assert.True(ItemDrops.GoldBaseForLevel(level + 1) > ItemDrops.GoldBaseForLevel(level),
                $"Expected GoldBaseForLevel({level + 1}) > GoldBaseForLevel({level})");

        Assert.InRange(ItemDrops.GoldBaseForLevel(1), 2.0, 3.0);
        Assert.InRange(ItemDrops.GoldBaseForLevel(68), 8.0, 11.0);
        Assert.InRange(ItemDrops.GoldBaseForLevel(83), 15.0, 20.0);
        Assert.InRange(ItemDrops.GoldBaseForLevel(100), 22.0, 30.0);

        // Concave IIR curve: meaningful gain at moderate IIR, saturating below 2.5x at extreme IIR.
        Assert.Equal(0.0, ItemDrops.GoldRarityScale(-100), precision: 6);
        Assert.Equal(0.5, ItemDrops.GoldRarityScale(-50), precision: 6);
        Assert.Equal(1.0, ItemDrops.GoldRarityScale(0), precision: 6);
        Assert.InRange(ItemDrops.GoldRarityScale(100), 1.35, 1.50);
        Assert.Equal(2.0, ItemDrops.GoldRarityScale(500), precision: 6);
        Assert.True(ItemDrops.GoldRarityScale(10_000) < 2.5);
        // Meticulous Appraiser (100% IIQ + 50% IIR -> 0% IIQ + 350% IIR) reduces gold as in retail.
        Assert.True(1.0 * ItemDrops.GoldRarityScale(350) < 2.0 * ItemDrops.GoldRarityScale(50));

        // Simulate realistic maps via ItemDrops.Gold to guard against runaway multiplier regressions.
        var drops = Game.Drops;
        long SimulateMap(int level, int normal, int magic, int rare, int unique, int iirPct, double iiqFactor, int seed)
        {
            var rng = new Random(seed);
            long total = 0;
            for (int i = 0; i < normal; i++) total += drops.Gold(level, MonsterRarity.Normal, rng, 1, iirPct, iiqFactor)?.Item.StackSize ?? 0;
            for (int i = 0; i < magic; i++) total += drops.Gold(level, MonsterRarity.Magic, rng, 1, iirPct, iiqFactor)?.Item.StackSize ?? 0;
            for (int i = 0; i < rare; i++) total += drops.Gold(level, MonsterRarity.Rare, rng, 1, iirPct, iiqFactor)?.Item.StackSize ?? 0;
            for (int i = 0; i < unique; i++) total += drops.Gold(level, MonsterRarity.Unique, rng, 1, iirPct, iiqFactor)?.Item.StackSize ?? 0;
            return total;
        }

        long t1White = SimulateMap(68, 350, 60, 9, 1, iirPct: 0, iiqFactor: 1.0, seed: 101);
        long t5HighMf = SimulateMap(72, 600, 150, 30, 2, iirPct: 500, iiqFactor: 3.0, seed: 202);
        long t16AlchAndGo = SimulateMap(83, 480, 110, 20, 1, iirPct: 50, iiqFactor: 1.8, seed: 303);
        Assert.InRange(t1White, 1_500, 3_500);
        Assert.InRange(t5HighMf, 20_000, 50_000);
        Assert.InRange(t16AlchAndGo, 10_000, 22_000);

        // Zero/negative multipliers produce no drop; extreme multipliers clamp to GoldCoin's MaxStack (50,000).
        Assert.Null(drops.Gold(83, MonsterRarity.Unique, new Random(7), multiplier: 0, increasedRarity: 500, quantityFactor: 3.0));
        Assert.Null(drops.Gold(83, MonsterRarity.Unique, new Random(7), multiplier: 1, increasedRarity: -100, quantityFactor: 3.0));
        var capped = drops.Gold(83, MonsterRarity.Unique, new Random(7), multiplier: 10_000, increasedRarity: 500, quantityFactor: 3.0);
        Assert.NotNull(capped);
        Assert.Equal(50_000, capped.Item.StackSize);
    }
}
