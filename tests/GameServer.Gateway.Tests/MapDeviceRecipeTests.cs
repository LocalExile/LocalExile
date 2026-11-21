using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class MapDeviceRecipeTests
{
    [Fact]
    public void Single_item_recipes_require_the_complete_input_set()
    {
        var catalog = Program.Items;
        var device = MapDevice.Load("data", row => catalog.Row(row)?.Hash, catalog);
        MapDevice.Slot Slot(string id)
        {
            var type = Assert.IsType<ItemCatalog.BaseType>(catalog.Find(id));
            Assert.Equal(id, type.Id);
            Assert.True(MapDevice.AcceptsItem(type.Hash, type.Class));
            return new(type.Hash, type.Class, type.Name, type.Row, [], 0) { Components = type.Components };
        }

        var audience = Slot("Metadata/Items/MapFragments/RitualBossFragment");
        var recipe = Assert.Single(MapRunCatalog.Load("data").Recipes, r => r.Id == "RitualBoss");
        Assert.Equal(audience.BaseRow, Assert.Single(recipe.Items));
        Assert.False(recipe.AnyOne);
        var opening = Assert.IsType<MapDevice.Opening>(device.Check([audience], null, catalog.Mods, out string why));
        Assert.Empty(why);
        Assert.Equal("AzmeriLeagueBoss", opening.Area.Id);
        Assert.Equal(83, opening.Area.Level);
        Assert.Equal(audience.BaseRow, opening.ItemRow);
        Assert.True(device.IsMapArea(opening.Area.Hash16));

        var key = Slot("Metadata/Items/MapFragments/ClassicVaultKey");
        var remnant = Slot("Metadata/Items/MapFragments/Crucible");
        foreach (var extra in new[] { key, remnant, audience })
        {
            Assert.Null(device.Check([audience, extra], null, catalog.Mods, out why));
            Assert.NotEmpty(why);
            Assert.Null(device.Check([extra, audience], null, catalog.Mods, out why));
            Assert.NotEmpty(why);
        }
        Assert.NotNull(device.Check([key], null, catalog.Mods, out why));
        Assert.Empty(why);

        // The Sacred Blossom alone opens Oshabi's grove at the area's own level (the recipe's AreaLevel is 0).
        var blossom = Slot("Metadata/Items/MapFragments/CurrencyHarvestBossKey");
        var harvest = Assert.Single(MapRunCatalog.Load("data").Recipes, r => r.Id == "HarvestBoss");
        Assert.Equal(blossom.BaseRow, Assert.Single(harvest.Items));
        opening = Assert.IsType<MapDevice.Opening>(device.Check([blossom], null, catalog.Mods, out why));
        Assert.Empty(why);
        Assert.Equal("HarvestLeagueBoss", opening.Area.Id);
        Assert.Equal(83, opening.Area.Level);
        Assert.Equal(blossom.BaseRow, opening.ItemRow);
        Assert.True(device.IsMapArea(opening.Area.Hash16));
        foreach (var extra in new[] { key, remnant, blossom })
        {
            Assert.Null(device.Check([blossom, extra], null, catalog.Mods, out why));
            Assert.NotEmpty(why);
            Assert.Null(device.Check([extra, blossom], null, catalog.Mods, out why));
            Assert.NotEmpty(why);
        }

        var pyramid = Slot("Metadata/Items/Maps/MapWorldsVaalPyramid");
        var vault = pyramid with { Rarity = ItemRarity.Unique, Corrupted = true };
        Assert.Equal("MapWorldsVaalPyramid", Assert.IsType<MapDevice.Opening>(device.Check([pyramid], out _)).Area.Id);
        var vaultArea = Assert.IsType<MapDevice.Opening>(device.Check([vault], out _)).Area;
        Assert.Equal("MapWorldsVaalPyramidUnique", vaultArea.Id);
        Assert.True(device.IsMapArea(vaultArea.Hash16));

        var conn = new ClientState(new(1, "recipe", 64, 64, 32, 32, null, []), 1, 1);
        uint WordFor(string name) => ClientTables.Table("Words")[
            ClientTables.Table("UniqueMaps").First(r => r.Text("Name") == name).Int("WordsKey")].UInt("HASH32");
        var keyMap = Slot("Metadata/Items/Maps/MapKeyTier1") with { Rarity = vault.Rarity };
        var named = keyMap with { UniqueNameWord = WordFor("Vaults of Atziri") };
        Assert.Equal(vaultArea.Hash16, Assert.IsType<MapDevice.Opening>(device.Check([named], out _)).Area.Hash16);
        foreach (string retired in new[] { "Untainted Paradise", "The Perandus Manor" })
        {
            var slot = keyMap with { UniqueNameWord = WordFor(retired) };
            var check = new MapOpening(conn, [slot], false, device.Check([slot], out why), why);
            PortalsFeature.CheckRetiredDeviceInputs(check);
            Assert.Null(check.Opening);
            Assert.NotEmpty(check.Reason);
        }

        foreach (var (family, area) in new[] { (1, "MapAtziri1"), (2, "MapAtziri2") })
        {
            var set = Enumerable.Range(1, 4).Select(n => Slot($"Metadata/Items/MapFragments/CurrencyVaalFragment{family}_{n}")).ToArray();
            foreach (var fragment in set)
            {
                var single = new MapOpening(conn, [fragment], false, device.Check([fragment], null, catalog.Mods, out why), why);
                PortalsFeature.CheckRetiredDeviceInputs(single);
                Assert.Null(single.Opening);
                Assert.NotEmpty(single.Reason);
            }
            var complete = new MapOpening(conn, set, false, device.Check(set, null, catalog.Mods, out why), why);
            PortalsFeature.CheckRetiredDeviceInputs(complete);
            Assert.Equal(area, complete.Opening!.Area.Id);
            Assert.Empty(complete.Reason);
        }
    }
}
