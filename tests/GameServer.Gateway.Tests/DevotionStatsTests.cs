using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class DevotionStatsTests
{
    [Theory]
    [InlineData(9, 0)]
    [InlineData(10, 2)]
    [InlineData(145, 28)]
    public void Per_ten_devotion_scales_with_whole_steps(int devotion, int expectedResistance)
    {
        var sources = new Dictionary<uint, int>
        {
            [Program.Stats.Key("base_devotion")!.Value] = devotion,
            [Program.Stats.Key("elemental_resistance_%_per_10_devotion")!.Value] = 2,
            [Program.Stats.Key("self_curse_duration_+%_per_10_devotion")!.Value] = -4,
        };

        DevotionStats.Apply(sources, Program.Stats);

        int steps = devotion / 10;
        Assert.Equal(expectedResistance, sources.GetValueOrDefault(Program.Stats.Key("base_resist_all_elements_%")!.Value));
        Assert.Equal(-4 * steps, sources.GetValueOrDefault(Program.Stats.Key("self_curse_duration_+%")!.Value));
        Assert.False(sources.ContainsKey(Program.Stats.Key("elemental_resistance_%_per_10_devotion")!.Value));
        Assert.False(sources.ContainsKey(Program.Stats.Key("self_curse_duration_+%_per_10_devotion")!.Value));
    }

    [Fact]
    public void No_devotion_removes_the_raw_per_devotion_stat()
    {
        var sources = new Dictionary<uint, int>
        {
            [Program.Stats.Key("elemental_resistance_%_per_10_devotion")!.Value] = 2,
        };

        DevotionStats.Apply(sources, Program.Stats);

        Assert.Empty(sources);
    }

    [Fact]
    public void Militant_faith_resistance_mod_reaches_the_resistance_pools()
    {
        TestSaves.Ensure();
        string character = "DevotionPool" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            // Socket 0 is 26725; 62429 is the Strength small in its radius. Militant Faith replaces it with +10 Devotion.
            conn.Passives = [26725, 62429];
            var items = PlayerInventories.For(character, Program.Items);
            // The timeless jewel requires level 20; its global mods only count while the item is active.
            var without = Program.PoolsFor(conn, 20);

            var jewel = TimelessJewelCatalog.Create(Program.Items, "Militant Faith", 2000, 1)!;
            var resistance = Program.Items.Mods.ById("ElementalResistancesPerDevotion")!;
            jewel = jewel with { Mods = [jewel.Mods[0], resistance.Roll(ModSection.Explicit, new Random(1))] };
            items.Add(PlayerInventories.PassiveJewels, 0, 0, jewel);

            var with = Program.PoolsFor(conn, 20);
            Assert.Equal(without.FireResistance + 2, with.FireResistance);
            Assert.Equal(without.ColdResistance + 2, with.ColdResistance);
            Assert.Equal(without.LightningResistance + 2, with.LightningResistance);
        }
        finally { PlayerInventories.Forget(character); }
    }
}
