using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class VendorEquipmentPurchaseTests
{
    [Fact]
    public void Direct_purchase_equips_swaps_and_preserves_payment_on_refusal_or_save_failure()
    {
        Saves.Open(Path.Combine(Path.GetTempPath(), "LocalExile-equip-" + Guid.NewGuid().ToString("N") + ".db"));
        var catalog = new ItemCatalog("data");
        string name = "VendorEquip" + Guid.NewGuid().ToString("N");
        var items = PlayerInventories.For(name, catalog);
        var stash = AccountStash.For(name, catalog);
        var wisdom = catalog.Find("Scroll of Wisdom")!;
        var ring = ItemCatalog.Create(catalog.Find("Iron Ring")!) with { Price = [(wisdom.Hash, 1)] };
        PlayerInventories.Shape? Shape(Item item) => catalog.ByHash(item.TypeHash) is { } t
            ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
        items.Add(0, 0, 0, ItemCatalog.Create(wisdom) with { StackSize = 10 });
        int consumed = 0;
        bool Consume() { consumed++; return true; }

        // Invalid slot and occupied cursor must refuse before consuming stock or money.
        Assert.Null(ItemMoves.PurchaseVendor(items, stash, stash, ring, catalog, Shape, Consume, PlayerInventories.Helmet));
        Assert.Equal(0, consumed);
        Assert.Equal(10, items.Count(0, wisdom.Hash));

        var first = ItemMoves.PurchaseVendor(items, stash, stash, ring, catalog, Shape, Consume, 6)!;
        Assert.Equal((byte)6, first.Equipped!.Row);
        Assert.Null(first.Equipped.Displaced);
        Assert.Empty(items.Items(PlayerInventories.Cursor));
        Assert.Equal(9, items.Count(0, wisdom.Hash));
        var second = ItemMoves.PurchaseVendor(items, stash, stash, ring, catalog, Shape, Consume, 6)!;
        Assert.Equal(first.Bought[0].Id, second.Equipped!.Displaced!.Id);
        Assert.Equal(first.Bought[0].Id, Assert.Single(items.Items(PlayerInventories.Cursor)).Id);
        Assert.Equal(second.Bought[0].Id, Assert.Single(items.Items(6)).Id);
        Assert.Null(ItemMoves.PurchaseVendor(items, stash, stash, ring, catalog, Shape, Consume, 7));
        Assert.Equal(2, consumed);
        Assert.Equal(8, items.Count(0, wisdom.Hash));

        // Clear the old ring, then force a write failure after payment. Reload must retain both items and currency.
        items.Remove(PlayerInventories.Cursor, first.Bought[0].Id);
        Saves.FailWrite = key => key == name;
        try
        {
            Assert.Throws<IOException>(() => ItemMoves.PurchaseVendor(items, stash, stash, ring, catalog, Shape, Consume, 7));
        }
        finally { Saves.FailWrite = null; }
        PlayerInventories.Forget(name);
        AccountStash.Forget(name);
        items = PlayerInventories.For(name, catalog);
        Assert.Equal(8, items.Count(0, wisdom.Hash));
        Assert.Equal(second.Bought[0].Id, Assert.Single(items.Items(6)).Id);
        Assert.Empty(items.Items(7));
        Assert.Empty(items.Items(PlayerInventories.Cursor));
    }
}
