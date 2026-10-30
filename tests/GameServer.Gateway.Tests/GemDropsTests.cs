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
