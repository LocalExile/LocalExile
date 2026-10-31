using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class UniqueStashFillTests
{
    static PlayerInventories.Shape? Shape(Item item) => Program.Items.ByHash(item.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;

    [Fact]
    public void Fill_uses_native_categories_and_survives_reload_without_duplicate_items()
    {
        Saves.Open(Path.Combine(Path.GetTempPath(), "LocalExile-unique-" + Guid.NewGuid().ToString("N") + ".db"));
        var uniques = new UniqueCatalog(Program.Items);
        string account = "UniqueFill" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, Program.Items);
        int tab = Enumerable.Range(0, stash.Count).Single(i => stash.TabType(i) == StashRules.Unique);
        var first = stash.FillUniques(tab, uniques, Shape);
        Console.WriteLine($"Unique fill: {first.Added} added; skipped {first.Skipped.Count}: {string.Join(", ", first.Skipped)}");
        Assert.Equal(0, first.Present);
        Assert.True(first.Added > 1000);
        var children = stash.UniqueChildren(tab);
        Assert.NotEmpty(children);
        var records = stash.Records(null);
        Assert.All(children, i => { Assert.NotEqual(0ul, records[i].HiddenKey); Assert.Equal(tab, records[i].Parent); });
        // The native Unique page constructs its item grids whenever a hidden record has a nonzero inventory ID,
        // even before its loaded bit is set. Never advertise an inventory that has not been created on this client.
        Assert.All(children, i => Assert.Equal(0u, records[i].Inventory));
        var loadedRecords = stash.Records(null, children.ToHashSet());
        Assert.All(children, i => Assert.Equal(stash.InventoryOf(i), loadedRecords[i].Inventory));
        var all = children.SelectMany(i => stash.Inventory(i).Items).ToArray();
        Assert.Equal(first.Added, all.Length);
        Assert.All(all, i => Assert.Equal(ItemRarity.Unique, i.Item.Rarity));
        Assert.Contains(all, i => i.Item.Components.Contains("Tincture"));
        Assert.Contains(all, i => Shape(i.Item)?.Class == "Map");
        var layout = UniqueStashLayout.Load("data");
        Assert.All(all, i => { var cell = layout.Find(i.Item)!; Assert.Equal(cell.X, i.X); Assert.Equal(cell.Y, i.Y); });
        var mageblood = all.Single(i => i.Item.NameWords.Contains(layout.Cells.Single(c => c.Name == "Mageblood").Word));
        int belt = children.Single(i => stash.Inventory(i).Items.Any(it => it.Id == mageblood.Id));
        Assert.Equal((byte)124, mageblood.X);
        stash.SetLastTab(belt);
        Assert.Equal(tab, stash.LastTab);
        AccountStash.Forget(account);
        stash = AccountStash.For(account, Program.Items);
        var second = stash.FillUniques(tab, uniques, Shape);
        Assert.Equal(0, second.Added);
        Assert.Equal(first.Added + first.Present, second.Present);
        Assert.Equal(first.Skipped, second.Skipped);
        Assert.Equal(all.Select(i => i.Id).Order(), stash.UniqueChildren(tab).SelectMany(i => stash.Inventory(i).Items).Select(i => i.Id).Order());
        var items = PlayerInventories.For(account + "Character", Program.Items);
        var taken = ItemMoves.StashToInventory(stash, items, belt, mageblood.Id, Shape);
        Assert.NotNull(taken);
        Assert.DoesNotContain(stash.Inventory(belt).Items, i => i.Id == mageblood.Id);
        Assert.NotNull(ItemMoves.InventoryToStash(stash, items, tab, mageblood.Id, Shape, out _));
        Assert.Contains(stash.Inventory(belt).Items, i => i.Id == mageblood.Id && i.X == mageblood.X);
        Assert.Equal(0, stash.FillUniques(tab, uniques, Shape).Added);
    }
}
