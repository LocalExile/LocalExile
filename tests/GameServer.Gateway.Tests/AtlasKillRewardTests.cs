using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class AtlasKillRewardTests
{
    [Fact]
    public void Map_tier_upgrade_runs_only_after_the_drop_roll_succeeds_and_caps_at_tier_16()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var drops = Game.DropsFor(f.Player);

        var miss = new SequenceRoll(0.99);
        Assert.Null(drops.Map(79, MonsterRarity.Rare, 12, miss, tierUpgradePermillage: 1000));
        Assert.Equal(1, miss.Calls);

        var upgraded = new SequenceRoll(0.0, 0.5, 0.99);
        var reward = drops.Map(79, MonsterRarity.Rare, 12, upgraded, tierUpgradePermillage: 1000);
        Assert.NotNull(reward);
        Assert.Equal("Metadata/Items/Maps/MapKeyTier13", reward.Type.Id);

        var capped = new SequenceRoll(0.0, 0.99);
        var t16 = drops.Map(83, MonsterRarity.Rare, 16, capped, tierUpgradePermillage: 1000);
        Assert.NotNull(t16);
        Assert.Equal("Metadata/Items/Maps/MapKeyTier16", t16.Type.Id);
    }

    [Fact]
    public void RollMapDropForKill_applies_drop_chance_per_mod_bonus_tier_upgrade_and_no_map_drops()
    {
        double rareBase = 0.05 * SystemSettings.Default.DropMultiplier("maps");
        double boostedRoll = rareBase * 1.5;

        ItemDrops.Drop? Roll(int tier, Dictionary<uint, int> stats, params double[] rolls)
        {
            using var f = new TriggeredSummonTests.Fixture();
            var area = new MapDevice.MapArea(f.Player.Layout.Hash, "MapWorldsStrand", "Strand", tier);
            f.Player.Instance.OpenedWith(new MapDevice.Run(f.Player.Character.Name ?? "atlas-test", area, new MapMods(stats)));
            return Game.RollMapDropForKill(f.Player, 67 + tier, MonsterRarity.Rare, tier, new SequenceRoll(rolls), 1);
        }

        Assert.Null(Roll(12, [], boostedRoll, 0.99));

        var boosted = Roll(12, new Dictionary<uint, int> { [Stat.MapMapItemDropChancePlusPct] = 100 }, boostedRoll, 0.99);
        Assert.NotNull(boosted);
        Assert.Equal("Metadata/Items/Maps/MapKeyTier12", boosted.Type.Id);

        var perMod = Roll(12, new Dictionary<uint, int>
        {
            [Stat.MapMapItemDropChancePlusPctPerMapMod] = 25,
            [Stat.MapNumberOfExplicitMods] = 4,
        }, boostedRoll, 0.99);
        Assert.NotNull(perMod);
        Assert.Equal("Metadata/Items/Maps/MapKeyTier12", perMod.Type.Id);

        var upgraded = Roll(12, new Dictionary<uint, int> { [Stat.MapTierBonusPermillage] = 1000 }, 0.0, 0.5, 0.99);
        Assert.NotNull(upgraded);
        Assert.Equal("Metadata/Items/Maps/MapKeyTier13", upgraded.Type.Id);

        var capped = Roll(16, new Dictionary<uint, int> { [Stat.MapTierBonusPermillage] = 1000 }, 0.0, 0.99);
        Assert.NotNull(capped);
        Assert.Equal("Metadata/Items/Maps/MapKeyTier16", capped.Type.Id);

        Assert.Null(Roll(12, new Dictionary<uint, int>
        {
            [Stat.MapMapItemDropChancePlusPct] = 1000,
            [Stat.MapMonsterNoMapDrops] = 1,
        }, 0.0));
    }

    sealed class SequenceRoll(params double[] rolls) : Random(467)
    {
        public int Calls { get; private set; }
        public override double NextDouble() => rolls[Math.Min(Calls++, rolls.Length - 1)];
        public override int Next(int minValue, int maxValue) => minValue;
    }
}
