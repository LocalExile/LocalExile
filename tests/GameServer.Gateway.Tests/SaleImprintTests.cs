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

}
