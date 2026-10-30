using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Item changes that touch the character's items and the stash (or one save twice): each is one transaction. The failure
// tests make one of its writes fail, as a full disk would, then load both saves again as a restarted Gateway would: every
// item must be in exactly one place, as it was before the change.
[Collection(TestSaves.FailWriteCollection)]
public class ItemMovesTests : IDisposable
{
    const uint WandType = 0xd9b1a12f, OrbType = 0x1234abcd;
    readonly string _folder = TestSaves.Catalog(new(WandType, "Wand", 1, 3), new(OrbType, "Orb", StackSize: 40));
    readonly string _character = $"Mover{Guid.NewGuid():N}"[..20];
    readonly string _account = $"Acc{Guid.NewGuid():N}"[..16];
    const byte Main = PlayerInventories.MainInventory, Cursor = PlayerInventories.Cursor;

    public ItemMovesTests() => TestSaves.Ensure();

    public void Dispose()
    {
        Saves.FailWrite = null;
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    ItemCatalog Catalog() => new(_folder);
    PlayerInventories Items() => PlayerInventories.For(_character, Catalog());
    AccountStash Stash() => AccountStash.For(_account, Catalog());

    // A restarted Gateway: both loaded again from the database.
    (PlayerInventories Items, AccountStash Stash) Reload()
    {
        PlayerInventories.Forget(_character);
        AccountStash.Forget(_account);
        return (Items(), Stash());
    }

    static Item Wand() => new(WandType, ["Base"], 0x40a1);
    static Item Orbs(int n) => new(OrbType, ["Base", "Stack"], 0x1111) { StackSize = (ushort)n };
    static PlayerInventories.Shape? Shapes(Item i) => i.TypeHash == OrbType ? new(1, 1, "StackableCurrency", 40) : new(1, 3, "Wand");

    // Writes of the save under this key fail (from the nth on, counting from 1).
    void FailWrites(string key, int from = 1)
    {
        int n = 0;
        Saves.FailWrite = k => k.Equals(key, StringComparison.OrdinalIgnoreCase) && ++n >= from;
    }

    static List<uint> Ids(IEnumerable<InventoryItem> items) => [.. items.Select(i => i.Id).Order()];

    InventoryItem InStash(AccountStash stash, Item item, int tab = 0, int x = 0, int y = 0) =>
        stash.Place(tab, x, y, new InventoryItem(1, 0, 0, item), Shapes, out _)!.Value.Placed;

    [Fact]
    public void Without_a_transaction_a_failed_second_write_loses_the_item()
    {
        // The control: the same failure with the two writes on their own (how moves worked before) loses the item, so the
        // failure tests below would see a loss.
        var (items, stash) = (Items(), Stash());
        var wand = InStash(stash, Wand());
        FailWrites(_character);

        stash.Take(0, wand.Id);
        Assert.ThrowsAny<IOException>(() => items.Put(Cursor, wand));
        Saves.FailWrite = null;

        var (again, stashAgain) = Reload();
        Assert.Empty(stashAgain.Inventory(0).Items);
        Assert.Empty(again.Items(Cursor));
    }

}
