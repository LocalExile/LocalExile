using GameServer.Core.Protocol.Instance;
using Xunit;

namespace GameServer.Gateway.Tests;

public class VendorRecipesTests
{
    static ItemCatalog Catalog => BenchCraftingTests.Setup.Value.Catalog;
    static readonly Lazy<VendorRecipes> Sales = new(() => new(Catalog, BenchCraftingTests.Data()));
    static Item Make(string name, ItemRarity rarity = ItemRarity.Normal, int level = 70, bool identified = true, int quality = 0, int count = 1)
    {
        var type = Catalog.Find(name)!;
        return ItemCatalog.Create(type) with { Rarity = rarity, ItemLevel = (byte)level, Identified = identified, Quality = (byte)quality, StackSize = (ushort)count };
    }
    static VendorRecipes.Quote Quote(params Item[] items) => Sales.Value.Evaluate(items.Select((i, n) => new InventoryItem((uint)n + 1, 0, 0, i)), new Random(27));
    static int Count(VendorRecipes.Quote quote, string name) => quote.Rewards.Where(i => Catalog.ByHash(i.TypeHash)!.Name == name).Sum(i => i.StackSize);
    static Item[] RareSet(int level, bool identified, int quality, string weapon = "Crude Bow") =>
        new[] { "Iron Hat", "Plate Vest", "Iron Gauntlets", "Iron Greaves", "Rustic Sash", "Amber Amulet", "Iron Ring", "Paua Ring", weapon }
            .Select(n => Make(n, ItemRarity.Rare, level, identified, quality)).ToArray();

    [Theory]
    [InlineData(59, "Orb of Chance")] [InlineData(60, "Chaos Orb")]
    public void Full_set_uses_lowest_item_level_and_accepts_two_handed_weapons(int level, string reward)
    {
        var set = RareSet(100, true, 0);
        set[0] = set[0] with { ItemLevel = (byte)level };
        Assert.Equal(1, Count(Quote(set), reward));
    }

}
