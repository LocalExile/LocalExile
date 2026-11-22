using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ScarabDropBlockTests
{
    [Fact]
    public void Unwavering_vision_blocks_random_and_explicit_map_scarab_rolls()
    {
        var drops = new ItemDrops(Program.Items);
        var disabled = new MapMods(new Dictionary<string, int> { ["map_disable_scarab_drops"] = 1 });
        var original = Assert.IsType<ItemDrops.Drop>(drops.Scarab(new Random(7), 83, MapMods.None));
        Assert.Null(drops.Scarab(new Random(7), 83, disabled));
        Assert.Equal(original.Type.Id, drops.Scarab(new Random(7), 83, MapMods.None)!.Type.Id);

        int Count(MapMods mods) => Enumerable.Range(1, 128).Sum(seed => drops.Roll(83, MonsterRarity.Normal,
            new Random(seed), 0, 10, 1, 16, mods).Count(drop => drop.Type.Id.Contains("/Scarabs/", StringComparison.Ordinal)));
        Assert.True(Count(MapMods.None) > 0);
        Assert.Equal(0, Count(disabled));
    }
}
