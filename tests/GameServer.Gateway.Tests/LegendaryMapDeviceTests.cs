using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// A legendary map (UniqueMaps) opens its own area, by the unique's name word, whatever base it is built on (#1092).
public sealed class LegendaryMapDeviceTests
{
    [Fact]
    public void Unique_map_opens_its_legendary_area_at_its_tier_level()
    {
        var device = MapDevice.Load("data", _ => null);
        var uniques = ClientTables.Table("UniqueMaps");
        var words = ClientTables.Table("Words");
        uint WordOf(string name) => words[uniques.First(r => r.Text("Name") == name).Int("WordsKey")].UInt("HASH32");
        var tier1 = new MapDevice.Slot(InstanceEntities.ObjectTypeHash("Metadata/Items/Maps/MapKeyTier1"), "MapKey", "Map (Tier 1)", 0, [], 0);

        var rewritten = device.Check([tier1 with { Rarity = ItemRarity.Unique, NameWords = [WordOf("Rewritten Distant Memory")] }], out string why);
        Assert.True(rewritten is not null, why);
        Assert.Equal("Synthesis_MapGuardian4", rewritten.Area.Id);
        Assert.Equal(15, rewritten.Area.Tier);
        Assert.Equal(82, rewritten.Area.Level);   // Maps' tier 15, not the WorldAreas level 68
        Assert.True(device.IsMapArea(rewritten.Area.Hash16));

        var machinarium = device.Check([tier1 with { Rarity = ItemRarity.Unique, NameWords = [WordOf("Doryani's Machinarium")] }], out why);
        Assert.Equal("MapWorldsMazeUnique", machinarium?.Area.Id);
        Assert.Equal(72, machinarium?.Area.Level);

        // The same name words on a non-unique map do not pick a legendary area.
        Assert.Null(device.UniqueAreaOf([]));
        var rare = device.Check([tier1 with { Rarity = ItemRarity.Rare, NameWords = [WordOf("Rewritten Distant Memory")] }], out _);
        Assert.NotEqual("Synthesis_MapGuardian4", rare?.Area.Id);
    }
}
