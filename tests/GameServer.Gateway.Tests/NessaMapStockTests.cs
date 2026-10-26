using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class NessaMapStockTests
{
    [Fact]
    public void Goods_contain_only_one_of_each_legacy_map_and_every_unique_identity()
    {
        var catalog = BenchCraftingTests.Setup.Value.Catalog;
        var stock = NessaMapStock.Build(catalog, BenchCraftingTests.Data());
        Assert.NotEmpty(stock);
        var ordinary = stock.Where(e => e.Item.Rarity == ItemRarity.Normal).ToList();
        Assert.Contains(ordinary, e => e.Type.Id.EndsWith("/MapWorldsBeach"));
        Assert.Equal(ordinary.Count, ordinary.Select(e => e.Name).Distinct().Count());
        Assert.All(ordinary, e => Assert.StartsWith("Metadata/Items/Maps/MapWorlds", e.Type.Id));
        var unique = stock.Where(e => e.Item.Rarity == ItemRarity.Unique).ToList();
        Assert.Equal(28, unique.Count);
        Assert.Equal(unique.Count, unique.Select(e => e.Name).Distinct().Count());
        Assert.Contains(unique, e => e.Name == "Vaults of Atziri");
        Assert.Contains(unique, e => e.Name == "The Tower of Ordeals");
        Assert.Contains(unique, e => e.Name == "Infused Beachhead");
        Assert.All(unique, e =>
        {
            Assert.Single(e.Item.NameWords);
            var restored = CharacterSaves.FromSaved(CharacterSaves.ToSaved(e.Item), catalog);
            Assert.Equal(e.Item.Rarity, restored.Rarity);
            Assert.Equal(e.Item.NameWords, restored.NameWords);
            Assert.Equal(e.Item.VisualIdentity, restored.VisualIdentity);
        });
        uint id = 1;
        var notes = new List<string>();
        var tabs = NpcShops.Tabs("Metadata/NPC/Act1/Nessa", catalog, () => id++, notes, BenchCraftingTests.Data());
        var goods = tabs.Where(t => t.Name.StartsWith("Goods")).ToList();
        Assert.True(goods.Count >= 2);
        var maps = goods.SelectMany(t => t.Items).Where(i => catalog.ByHash(i.Item.TypeHash)?.Class == "Map").ToList();
        Assert.Equal(stock.Count, maps.Count);
        Assert.All(maps, i => Assert.Contains(i.Item.Rarity, new[] { ItemRarity.Normal, ItemRarity.Unique }));
        Assert.Contains(goods.SelectMany(t => t.Items), i => catalog.ByHash(i.Item.TypeHash)?.Name == "Small Life Flask");
        Assert.Contains(tabs, t => t.Name == "Red Support");
    }
}
