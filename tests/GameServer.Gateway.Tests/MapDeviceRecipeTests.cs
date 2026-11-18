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
    }
}
