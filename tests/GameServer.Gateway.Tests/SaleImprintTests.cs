using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The sell window, as in 3.29: an offered item keeps its inventory cell (a greyed imprint with its size and collision)
// until the sale ends, so cancelling, closing, a disconnect or a crash always leave every item where it was.
public class SaleImprintTests : IDisposable
{
    const uint WandType = 0xd9b1a12f, OrbType = 0x1234abcd;
    readonly string _folder = TestSaves.Catalog(new TestSaves.Type(WandType, "Wand", 1, 3), new TestSaves.Type(OrbType, "Orb", StackSize: 40));
    readonly string _character = $"Seller{Guid.NewGuid():N}"[..20];
    const byte Main = PlayerInventories.MainInventory, Cursor = PlayerInventories.Cursor;

    public SaleImprintTests() => TestSaves.Ensure();

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    PlayerInventories Items() => PlayerInventories.For(_character, new ItemCatalog(_folder));
    PlayerInventories Reload()
    {
        PlayerInventories.Forget(_character);
        return Items();
    }

    static Item Wand() => new(WandType, ["Base"], 0x40a1);
    static Item Orbs(int n) => new(OrbType, ["Base", "Stack"], 0x1111) { StackSize = (ushort)n };
    static PlayerInventories.Shape? Shapes(Item i) => i.TypeHash == OrbType ? new(1, 1, "StackableCurrency", 40) : new(1, 3, "Wand");

    [Fact]
    public void Offered_currency_cannot_be_offered_twice_or_spent_and_cancel_restores_its_balance()
    {
        var items = Items();
        var currency = items.Add(Main, 2, 1, Orbs(10));
        Assert.NotNull(items.Offer(Main, currency.Id, 0, 0, Shapes));
        Assert.Null(items.Offer(Main, currency.Id, 1, 0, Shapes));
        Assert.Null(items.PickUp(Main, currency.Id));
        Assert.Equal(0, items.Count(Main, OrbType));
        var restored = Assert.Single(items.CancelOffers());
        Assert.Equal(currency, restored.Item);
        Assert.Equal(10, items.Count(Main, OrbType));
        Assert.Empty(items.CancelOffers());
        Assert.Equal(10, Reload().Count(Main, OrbType));
    }

    [Fact]
    public void An_offered_item_keeps_its_cell_and_nothing_can_take_it()
    {
        var items = Items();
        var wand = items.Add(Main, 4, 1, Wand());

        Assert.Equal((Main, wand), items.Offer(Main, wand.Id, 0, 0, Shapes));

        Assert.Equal([(wand.Id, (byte)0, (byte)0)], items.Offered().Select(i => (i.Id, i.X, i.Y)));   // at its cell in the offer
        // Its inventory cell is still taken: a held item can't be put on it, and nothing can move or use it.
        items.Put(Cursor, new InventoryItem(items.NewItemId(), 0, 0, Wand()));
        Assert.Null(items.Place(Main, 4, 1, Shapes, out string why));
        Assert.Contains("offered", why);
        Assert.Null(items.RowOf(wand.Id));
        Assert.Null(items.Remove(Main, wand.Id));
        Assert.Null(items.Offer(Main, wand.Id, 1, 0, Shapes));   // once
    }

    [Fact]
    public void Cancelling_puts_every_offered_item_back_in_its_cell()
    {
        var items = Items();
        var wand = items.Add(Main, 4, 1, Wand());
        var orbs = items.Add(Main, 7, 0, Orbs(12));
        items.Offer(Main, wand.Id, 0, 0, Shapes);
        items.Offer(Main, orbs.Id, 1, 0, Shapes);

        var back = items.CancelOffers();

        Assert.Equal([(Main, wand), (Main, orbs)], back.OrderBy(b => b.Item.Id));
        Assert.Empty(items.Offered());
        Assert.Equal(Main, items.RowOf(wand.Id));
    }

    [Fact]
    public void A_disconnect_or_crash_during_the_sale_leaves_every_item_where_it_was()
    {
        var items = Items();
        var wand = items.Add(Main, 4, 1, Wand());
        items.Offer(Main, wand.Id, 0, 0, Shapes);

        // The save never lost it: a restarted Gateway loads it in its cell, and nothing is offered.
        var again = Reload();
        Assert.Equal([(wand.Id, (byte)4, (byte)1)], again.Items(Main).Select(i => (i.Id, i.X, i.Y)));
        Assert.Empty(again.Offered());
        // Without a restart (a new connection), the login cancels it.
        items = Items();
        items.Offer(Main, wand.Id, 0, 0, Shapes);
        Assert.Single(items.CancelOffers());
    }

    [Fact]
    public void A_held_item_offered_waits_in_the_cell_it_was_picked_up_from()
    {
        var items = Items();
        var wand = items.Add(Main, 6, 2, Wand());
        items.PickUp(Main, wand.Id);

        var offered = items.Offer(Cursor, wand.Id, 0, 0, Shapes);

        Assert.Equal((Main, (byte)6, (byte)2), (offered!.Value.Row, offered.Value.Item.X, offered.Value.Item.Y));
        Assert.Empty(items.Items(Cursor));
        // Saved there: a crash now leaves it in that cell, not on the cursor.
        Assert.Equal([(wand.Id, (byte)6, (byte)2)], Reload().Items(Main).Select(i => (i.Id, i.X, i.Y)));
    }

    [Fact]
    public void Accepting_takes_the_offer_and_gives_the_reward_in_one_save()
    {
        var items = Items();
        var wand = items.Add(Main, 0, 0, Wand());
        var kept = items.Add(Main, 1, 0, Wand());
        items.Offer(Main, wand.Id, 0, 0, Shapes);

        var sold = items.Sell(Orbs(1), Shapes);

        Assert.NotNull(sold);
        var again = Reload();
        Assert.DoesNotContain(again.Items(Main), i => i.Id == wand.Id);
        Assert.Contains(again.Items(Main), i => i.Id == kept.Id);
        Assert.Equal(1, again.Items(Main).Single(i => i.Item.TypeHash == OrbType).Item.StackSize);
        Assert.Empty(again.Offered());
    }

    [Fact]
    public void Selling_a_full_inventory_puts_the_reward_in_the_cells_the_offer_freed()
    {
        var items = Items();
        // 12 x 5, full: a wand at the top of every column, full orb stacks below.
        for (byte x = 0; x < 12; x++) items.Add(Main, x, 0, Wand());
        for (byte x = 0; x < 12; x++)
            for (byte y = 3; y < 5; y++) items.Add(Main, x, y, Orbs(40));
        var first = items.Items(Main).First(i => i.Item.TypeHash == WandType);
        items.Offer(Main, first.Id, 0, 0, Shapes);
        Assert.Null(items.FreeCell(Main, 1, 3, Shapes));   // the imprint keeps the cell while the sale is open

        var sold = items.Sell(Orbs(1), Shapes);

        Assert.NotNull(sold);
        Assert.Equal((first.X, first.Y), (sold!.Added.Single().X, sold.Added.Single().Y));
    }
}
