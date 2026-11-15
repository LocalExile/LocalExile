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
        Assert.Equal(opening.Area, device.AreaOfFragment(audience.TypeHash));
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

        // Constructor-supplied lookup metadata cannot bypass the recipe's complete-input validation.
        var lookupOnly = new MapDevice(new Dictionary<uint, MapDevice.MapArea>(),
            byFragment: new Dictionary<uint, MapDevice.MapArea> { [audience.TypeHash] = opening.Area });
        Assert.Equal(opening.Area, lookupOnly.AreaOfFragment(audience.TypeHash));
        Assert.Null(lookupOnly.Check([audience], out _));
        Assert.Null(lookupOnly.Check([audience, key], out _));
    }
}
