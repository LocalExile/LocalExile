using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class BenchBalanceTests
{
    [Fact]
    public void Display_uses_the_current_personal_stash_balance_without_counting_inventory_twice()
    {
        TestSaves.Ensure();
        var (catalog, _, bench, types) = BenchCraftingTests.Setup.Value;
        string name = "Balance" + Guid.NewGuid().ToString("N");
        var items = PlayerInventories.For(name, catalog);
        var stash = AccountStash.For(name + "Account", catalog);
        var recipe = bench.All.First(r => r.Mod is not null && r.Classes.Contains(25) && r.Costs.Count == 1 && r.Costs[0].Amount >= 6);
        var (hash, cost) = recipe.Costs.Single();
        Item Currency(int n) => ItemCatalog.Create(catalog.ByHash(hash)!) with { StackSize = (ushort)n };
        PlayerInventories.Shape? Shape(Item i) => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
        stash.AddStacked(0, Currency(cost - 2), Shape, out _);
        stash.AddStacked(1, Currency(5), Shape, out _);
        items.Add(PlayerInventories.MainInventory, 0, 0, Currency(2));
        items.Add(PlayerInventories.CraftingBench, 0, 0, ItemCatalog.Create(types.Single(t => t.Class == "Helmet")));
        long Held(IReadOnlyList<InventoryItem> snapshot) => snapshot.Where(i => i.Item.TypeHash == hash).Sum(i => (long)i.Item.StackSize);
        var before = BenchBalance.Snapshot(stash, catalog, bench);
        Assert.Equal(cost + 3, Held(before));
        Assert.Equal(cost + 5, Held(before) + items.Count(PlayerInventories.MainInventory, hash));
        Assert.NotNull(ItemMoves.CraftAtBench(items, stash, bench, recipe.Row, new Random(1)));
        Assert.Equal(5, Held(BenchBalance.Snapshot(stash, catalog, bench)));
        Assert.Equal(0, items.Count(PlayerInventories.MainInventory, hash));
        Assert.Null(PlayerInventories.Row(InstanceBench.BalanceInventoryId));
        Assert.Null(stash.TabOf(InstanceBench.BalanceInventoryId));
        Assert.All(before, view => Assert.DoesNotContain(view.Id, items.Items(PlayerInventories.MainInventory).Select(i => i.Id)));
    }

    [Fact]
    public void Ctrl_click_moves_an_item_onto_the_empty_bench_and_back_keeping_its_id()
    {
        TestSaves.Ensure();
        var (catalog, _, _, types) = BenchCraftingTests.Setup.Value;
        var items = PlayerInventories.For("BenchMove" + Guid.NewGuid().ToString("N"), catalog);
        PlayerInventories.Shape? Shape(Item i) => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
        var ring = items.Add(PlayerInventories.MainInventory, 3, 2, ItemCatalog.Create(types.First(t => t.Class == "Ring")));
        var other = items.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(types.First(t => t.Class == "Amulet")));

        var into = ItemMoves.MoveToBench(items, ring.Id, true, Shape);
        Assert.NotNull(into);
        Assert.Equal(ring.Id, Assert.Single(items.Items(PlayerInventories.CraftingBench)).Id);
        Assert.DoesNotContain(items.Items(PlayerInventories.MainInventory), i => i.Id == ring.Id);
        Assert.Null(ItemMoves.MoveToBench(items, other.Id, true, Shape));   // the slot is taken
        Assert.Null(ItemMoves.MoveToBench(items, ring.Id, true, Shape));    // no longer in the inventory

        var back = ItemMoves.MoveToBench(items, ring.Id, false, Shape);
        Assert.NotNull(back);
        Assert.Equal(PlayerInventories.MainInventory, back.To);
        Assert.Empty(items.Items(PlayerInventories.CraftingBench));
        Assert.Contains(items.Items(PlayerInventories.MainInventory), i => i.Id == ring.Id);
    }

}
