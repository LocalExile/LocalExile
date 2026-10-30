using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class NessaFullGemStockTests
{
    [Fact]
    public void Full_stock_ignores_quest_filter_preserves_variants_and_paginates_every_colour()
    {
        var catalog = BenchCraftingTests.Setup.Value.Catalog;
        uint next = 1;
        var notes = new List<string>();
        var tabs = NpcShops.Tabs("Metadata/NPC/Act1/Nessa", catalog, () => next++, notes,
            BenchCraftingTests.Data(), gemStock: [], fullGemStock: true);
        var all = tabs.SelectMany(t => t.Items).ToList();
        Assert.Equal(all.Count, all.Select(i => i.Id).Distinct().Count());
        foreach (var (colour, label) in new[] { (1, "Red"), (2, "Green"), (3, "Blue"), (4, "White") })
            foreach (bool support in new[] { false, true })
            {
                if (colour == 4 && support) continue; // Both kinds belong in White Skills.
                string name = $"{label} {(support ? "Support" : "Skills")}";
                var pages = tabs.Where(t => t.Name == name || t.Name.StartsWith(name + " ")).ToList();
                var stock = pages.SelectMany(t => t.Items).ToList();
                var expected = catalog.FullShopGems(colour, support)
                    .Concat(colour == 4 ? catalog.FullShopGems(4, true) : [])
                    .Select(g => (g.Type.Hash, g.Variant.Index)).Order().ToArray();
                Assert.Equal(expected, stock.Select(i => (i.Item.TypeHash, i.Item.GemVariant)).Order());
                Assert.All(pages, p => Assert.InRange(p.Items.Count, 0, 144));
                if (expected.Length > 144) Assert.True(pages.Count > 1);
                Assert.All(stock, i =>
                {
                    Assert.InRange(i.X, (byte)0, (byte)11);
                    Assert.InRange(i.Y, (byte)0, (byte)11);
                    Assert.NotEmpty(i.Item.Price);
                    var saved = CharacterSaves.FromSaved(CharacterSaves.ToSaved(i.Item), catalog);
                    Assert.Equal(i.Item.GemVariant, saved.GemVariant);
                    Assert.Equal(catalog.GemName(i.Item), catalog.GemName(saved));
                    Assert.NotEmpty(i.Item.Payload());
                });
                Assert.All(pages, p => Assert.Equal(p.Items.Count, p.Items.Select(i => (i.X, i.Y)).Distinct().Count()));
            }
        Assert.Contains(all, i => catalog.GemName(i.Item) == "Ice Nova of Frostbolts" && i.Item.GemVariant > 0);
        Assert.Contains(all, i => catalog.GemName(i.Item) == "Vaal Fireball");
        Assert.DoesNotContain(tabs, t => t.Name.StartsWith("White Support"));
        Assert.Empty(Assert.Single(tabs, t => t.Name == "Skills").Items);
        Assert.DoesNotContain(notes, n => n.StartsWith("no room"));
    }

}
