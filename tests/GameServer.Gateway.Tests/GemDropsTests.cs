using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Gems as monster drops (ItemDrops): a small share of the item drops, only gems whose DropLevel the monster meets, and
// unlevelled like a vendor's gem.
public class GemDropsTests
{
    static readonly Lazy<(ItemCatalog Catalog, ItemDrops Drops)> Setup = new(() =>
    {
        string data = BenchCraftingTests.Data();
        var catalog = new ItemCatalog(data);
        return (catalog, new ItemDrops(catalog, new ItemRolls(catalog, data), data));
    });

    static List<(ItemDrops.Drop Drop, ItemCatalog.BaseType Type, ItemCatalog.GemType Gem)> Gems(IEnumerable<ItemDrops.Drop> drops)
    {
        var catalog = Setup.Value.Catalog;
        return [.. drops.Select(d => (Drop: d, Type: catalog.ByHash(d.Item.TypeHash)!))
            .Where(d => catalog.Gem(d.Type) is not null)
            .Select(d => (d.Drop, d.Type, catalog.Gem(d.Type)!))];
    }

    // The quantity multiplier scales how many items roll: above 1 too (a mod's), not only Ruthless's 0.2.
    [Fact]
    public void Item_quantity_scales_the_number_of_drops_both_ways()
    {
        var (_, drops) = Setup.Value;
        int Total(double quantity) => Enumerable.Range(0, 300)
            .Sum(seed => drops.Roll(40, MonsterRarity.Rare, new Random(seed), quantityMultiplier: quantity).Count);
        int ordinary = Total(1);
        Assert.InRange(Total(100), ordinary * 90, ordinary * 110);
        Assert.InRange(Total(2.5), ordinary * 2.2, ordinary * 2.8);
        Assert.InRange(Total(0.2), ordinary * 0.1, ordinary * 0.3);
        Assert.Equal(ordinary, Total(1));
    }

    [Fact]
    public void Gems_drop_unlevelled_and_are_part_of_the_item_drops()
    {
        var (_, drops) = Setup.Value;
        var gems = Gems(Enumerable.Range(0, 4000).SelectMany(seed =>
            drops.Roll(80, MonsterRarity.Normal, new Random(seed))));
        Assert.NotEmpty(gems);
        Assert.All(gems, g =>
        {
            Assert.InRange(g.Type.DropLevel, 1, 80);
            Assert.Equal(1, g.Drop.Item.ItemLevel);
            Assert.Equal(0, g.Drop.Item.GemExperience);
            Assert.Equal(0, g.Drop.Item.Quality);
            Assert.Empty(g.Drop.Item.Sockets);
            Assert.Equal(ItemRarity.Normal, g.Drop.Item.Rarity);
            Assert.True(g.Drop.Item.Identified);
            Assert.False(g.Gem.IsVaalOrAwakened);
            Assert.DoesNotContain("Royale", g.Type.Id);
            Assert.DoesNotContain("Graft", g.Type.Id);
            Assert.NotEqual("...", g.Type.Name);
        });
    }

}
