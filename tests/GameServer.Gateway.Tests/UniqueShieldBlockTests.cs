using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>A unique shield's own "+X% Chance to Block" is a local modifier, and Kongming's removes the base block.</summary>
public class UniqueShieldBlockTests
{
    [Fact]
    public void Unique_shields_add_their_local_block_and_Kongmings_has_none()
    {
        var items = new ItemCatalog("data");
        var uniques = new UniqueCatalog(items);
        var aegis = Assert.IsType<Item>(uniques.Create("Aegis Aurora", new Random(17)));
        var aegisType = Assert.IsType<ItemCatalog.BaseType>(items.ByHash(aegis.TypeHash));
        int baseBlock = items.ShieldBlock(aegisType);
        Assert.True(baseBlock > 0);
        Assert.Equal(baseBlock + ItemModStats.Local(aegis, items.Mods).GetValueOrDefault("local_additional_block_chance_%"),
            ItemModStats.ShieldBlock(items, aegis, aegisType));
        Assert.True(ItemModStats.ShieldBlock(items, aegis, aegisType) > baseBlock);

        var kongming = Assert.IsType<Item>(uniques.Create("Kongming's Stratagem", new Random(17)));
        var kongmingType = Assert.IsType<ItemCatalog.BaseType>(items.ByHash(kongming.TypeHash));
        Assert.True(items.ShieldBlock(kongmingType) > 0);
        Assert.Equal(0, ItemModStats.ShieldBlock(items, kongming, kongmingType));
    }
}
