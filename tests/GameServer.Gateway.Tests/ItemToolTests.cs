using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ItemToolTests
{
    static ItemCatalog Catalog => BenchCraftingTests.Setup.Value.Catalog;
    static ItemRolls Rolls => BenchCraftingTests.Setup.Value.Rolls;
    static ItemCatalog.BaseType Ring => Catalog.Find("Two-Stone Ring")!;

    [Fact]
    public void Explicit_mods_are_kept_with_their_chosen_values()
    {
        var mod = Catalog.Mods.All.First(m => m.Domain == Ring.ModDomain && m.GenerationType == ItemRolls.Prefix &&
            m.Stats.Count == 1 && m.Stats[0].Max > m.Stats[0].Min && !m.EssenceOnly);
        int value = mod.Stats[0].Max;
        var built = ItemTool.Build(Catalog, Rolls, Ring, ItemRarity.Rare, new ItemTool.CreateRequest(
            1, Ring.Hash, "Rare", 100, 20, 1, [new ItemTool.ModChoice(mod.Hash, [value])]), new Random(1));

        Assert.Null(built.Error);
        Assert.Equal(ItemRarity.Rare, built.Item!.Rarity);
        Assert.Equal(20, built.Item.Quality);
        Assert.Contains(built.Item.Mods, m => m.Section == ModSection.Explicit && m.Hash == mod.Hash && m.Values[0] == value);
    }

    [Fact]
    public void Normal_items_reject_explicit_mods_and_out_of_range_values_are_refused()
    {
        var mod = Catalog.Mods.All.First(m => m.Domain == Ring.ModDomain && m.GenerationType == ItemRolls.Suffix && !m.EssenceOnly);
        var normal = ItemTool.Build(Catalog, Rolls, Ring, ItemRarity.Normal, new ItemTool.CreateRequest(
            1, Ring.Hash, "Normal", 100, 0, 1, [new ItemTool.ModChoice(mod.Hash, null)]), new Random(2));
        Assert.NotNull(normal.Error);

        var outOfRange = ItemTool.Build(Catalog, Rolls, Ring, ItemRarity.Magic, new ItemTool.CreateRequest(
            1, Ring.Hash, "Magic", 100, 0, 1, [new ItemTool.ModChoice(mod.Hash, [int.MinValue])]), new Random(2));
        Assert.NotNull(outOfRange.Error);
    }

    [Fact]
    public void Rare_item_without_chosen_mods_is_rolled()
    {
        var built = ItemTool.Build(Catalog, Rolls, Ring, ItemRarity.Rare,
            new ItemTool.CreateRequest(1, Ring.Hash, "Rare", 100, 0, 1, []), new Random(3));

        Assert.Null(built.Error);
        Assert.Equal(ItemRarity.Rare, built.Item!.Rarity);
        Assert.InRange(built.Item.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
    }
}
