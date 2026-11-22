using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ScarabQuantityTests
{
    [Fact]
    public void Atlas_scarab_quantity_adds_increased_values_and_scales_the_map_roll()
    {
        const string stat = "map_+%_increased_scarab_drops";
        MapMods With(int value) => new(new Dictionary<string, int> { [stat] = value });
        Assert.Equal(1, ItemDrops.ScarabQuantityFactor(MapMods.None));
        Assert.Equal(1.04, ItemDrops.ScarabQuantityFactor(With(4)), 6);
        Assert.Equal(1.08, ItemDrops.ScarabQuantityFactor(With(4 + 4)), 6);
        Assert.Equal(0, ItemDrops.ScarabQuantityFactor(With(-100)));

        var drops = new ItemDrops(Program.Items);
        int Count(MapMods mods) => Enumerable.Range(1, 512).Sum(seed =>
            drops.Roll(83, MonsterRarity.Normal, new Random(seed), 0, 1, 1, 16, mods)
                .Count(drop => drop.Type.Id.Contains("/Scarabs/", StringComparison.Ordinal)));
        int baseline = Count(MapMods.None);
        Assert.True(baseline > 0);
        Assert.True(Count(With(400)) > baseline);
        Assert.Equal(0, Count(With(-100)));
    }
}
