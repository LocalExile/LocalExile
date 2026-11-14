using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class SpecialStashStackTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    static PlayerInventories.Shape? Shape(ItemCatalog catalog, Item item) => catalog.ByHash(item.TypeHash) is { } t
        ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;

    [Fact]
    public void Dedicated_slots_fill_and_reload_single_inventory_fragments_and_allflames_without_collisions()
    {
        TestEnvironment.OpenOwnSaves("specialstash");
        var catalog = new ItemCatalog("data");
        string account = "SpecialStash" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, catalog);
        var tabs = new[] { StashRules.Fragment, StashRules.Delirium }
            .Select(type => Enumerable.Range(0, stash.Count).Single(i => stash.TabType(i) == type)).ToArray();
        foreach (int tab in tabs)
        {
            Assert.Equal(stash.TabType(tab) == StashRules.Fragment ? 0x16 : 0x1e, stash.Inventory(tab).Slot.Kind);
            var fill = stash.FillSlots(tab, 10, h => catalog.ByHash(h) is { Supported: true } t ? ItemCatalog.Create(t) : null,
                i => Shape(catalog, i), out string why);
            Assert.NotNull(fill);
            Assert.Equal("", why);
            Assert.Equal(stash.Inventory(tab).Items.Count, stash.Inventory(tab).Items.Select(i => (i.X, i.Y)).Distinct().Count());
        }
        string[] names = ["Simulacrum", "Offering to the Goddess", "The Maven's Writ", "Divine Vessel", "Sacred Blossom"];
        var selected = tabs.SelectMany(tab => stash.Inventory(tab).Items).Where(i => names.Contains(catalog.ByHash(i.Item.TypeHash)!.Name)
            || catalog.ByHash(i.Item.TypeHash)!.Name.Contains("Emblem") || catalog.ByHash(i.Item.TypeHash)!.Name.EndsWith("Breachstone")
            || catalog.ByHash(i.Item.TypeHash)!.Id.Contains("AllflamePack")).ToArray();
        foreach (string name in names) Assert.Contains(selected, i => catalog.ByHash(i.Item.TypeHash)!.Name == name);
        Assert.All(selected, i => Assert.Equal(10, i.Item.StackSize));
        var embers = selected.Where(i => catalog.ByHash(i.Item.TypeHash)!.Id.Contains("AllflamePack")).ToArray();
        Assert.Equal(8, embers.Length);
        Assert.All(embers, i => Assert.Equal(1, i.Y));
        Assert.Equal(Enumerable.Range(3, 8), embers.Select(i => (int)i.X).Order());
        // A column reused by Allflame on row 1 must still hold its Vaal fragment on row 0.
        Assert.Contains(stash.Inventory(tabs[0]).Items, i => i.X == 3 && i.Y == 0);
        var before = tabs.SelectMany(tab => stash.Inventory(tab).Items).ToArray();
        AccountStash.Forget(account);
        stash = AccountStash.For(account, catalog);
        Assert.Equal(before.Select(i => (i.Id, i.X, i.Y, i.Item.TypeHash, i.Item.StackSize)),
            tabs.SelectMany(tab => stash.Inventory(tab).Items).Select(i => (i.Id, i.X, i.Y, i.Item.TypeHash, i.Item.StackSize)));
        AccountStash.Forget(account);
    }

    [Fact]
    public void One_per_inventory_item_merges_in_its_stash_slot_and_withdraws_without_overstacking_or_loss()
    {
        TestEnvironment.OpenOwnSaves("specialmoves");
        var catalog = new ItemCatalog("data");
        string account = "SpecialMoves" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, catalog);
        var items = PlayerInventories.For(account, catalog);
        PlayerInventories.Shape? ShapeOf(Item i) => Shape(catalog, i);
        var type = catalog.All.Single(t => t.Name == "Simulacrum" && t.Components.Contains("Stack"));
        Assert.Equal(1, type.MaxStack);
        int tab = Enumerable.Range(0, stash.Count).Single(i => stash.TabType(i) == StashRules.Delirium);
        for (int n = 0; n < 2; n++)
        {
            var inventory = items.AddStacked(PlayerInventories.MainInventory, ItemCatalog.Create(type), ShapeOf).Added.Single();
            Assert.NotNull(ItemMoves.InventoryToStash(stash, items, tab, inventory.Id, ShapeOf, out _));
        }
        var stack = stash.Inventory(tab).Items.Single();
        Assert.Equal(2, stack.Item.StackSize);
        var held = items.Put(PlayerInventories.Cursor, new(items.NewItemId(), 0, 0, ItemCatalog.Create(type)));
        var merged = ItemMoves.HeldToStash(stash, items, tab, stack.X, stack.Y, false, ShapeOf, out _)!;
        Assert.True(merged.Merged);
        Assert.Null(merged.Held);
        Assert.Equal(3, merged.InTab.Item.StackSize);
        var fill = stash.FillSlots(tab, 10000, h => h == type.Hash ? ItemCatalog.Create(type) : null, ShapeOf, out _)!;
        Assert.Equal(StashRules.SlotStack, fill.Changed.Single().Item.StackSize);
        var split = ItemMoves.SplitFromStash(stash, items, tab, stack.Id, 100, ShapeOf)!.Value;
        Assert.Equal(1, split.Part.Item.StackSize);
        Assert.Equal(StashRules.SlotStack - 1, split.Left!.Item.StackSize);
        Assert.NotNull(ItemMoves.HeldToStash(stash, items, tab, stack.X, stack.Y, false, ShapeOf, out _));
        var one = ItemMoves.StashToInventory(stash, items, tab, stack.Id, ShapeOf)!;
        Assert.Single(one.InventoryAdded);
        Assert.Equal(1, one.InventoryAdded[0].Item.StackSize);
        var bulk = ItemMoves.StashToInventory(stash, items, tab, stack.Id, ShapeOf, all: true)!;
        Assert.True(bulk.InventoryAdded.Count > 1);
        Assert.All(items.Items(PlayerInventories.MainInventory), i => Assert.Equal(1, i.Item.StackSize));
        int total = items.Items(PlayerInventories.MainInventory).Sum(i => (int)i.Item.StackSize) + stash.Inventory(tab).Items.Sum(i => (int)i.Item.StackSize);
        Assert.Equal(StashRules.SlotStack, total);
        AccountStash.Forget(account);
        stash = AccountStash.For(account, catalog);
        Assert.Equal(total, items.Items(PlayerInventories.MainInventory).Sum(i => (int)i.Item.StackSize) + stash.Inventory(tab).Items.Sum(i => (int)i.Item.StackSize));
        PlayerInventories.Forget(account);
        AccountStash.Forget(account);
    }
}
