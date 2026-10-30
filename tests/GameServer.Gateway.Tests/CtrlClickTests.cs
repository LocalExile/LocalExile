using GameServer.Core.Protocol.Instance;
using static GameServer.Gateway.Tests.StashRulesTests;

namespace GameServer.Gateway.Tests;

// Ctrl + click: currency into its affinity tab's slot, items out of the stash onto the inventory's stacks, buying from a
// shop; each one transaction, and with a write made to fail, nothing moves.
[Collection(TestSaves.FailWriteCollection)]
public class CtrlClickTests : IDisposable
{
    readonly string _folder = TestSaves.Catalog(new TestSaves.Type(WandType, "Wand", 1, 3), new TestSaves.Type(ScrollType, "Scroll", StackSize: 40),
        new TestSaves.Type(MapType, "Map"), new TestSaves.Type(OrbType, "Orb", StackSize: 20));
    readonly string _character = $"Ctrl{Guid.NewGuid():N}"[..20];
    readonly string _account = $"Ctrl{Guid.NewGuid():N}"[..16];
    const byte Main = PlayerInventories.MainInventory;

    public CtrlClickTests() => TestSaves.Ensure();

    public void Dispose()
    {
        Saves.FailWrite = null;
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    ItemCatalog Catalog() => new(_folder) { Stash = Rules() };
    PlayerInventories Items() => PlayerInventories.For(_character, Catalog());
    AccountStash Stash() => AccountStash.For(_account, Catalog());
    (PlayerInventories Items, AccountStash Stash) Reload()
    {
        PlayerInventories.Forget(_character);
        AccountStash.Forget(_account);
        return (Items(), Stash());
    }

    [Fact]
    public void Currency_goes_into_its_slot_of_the_currency_tab_onto_what_is_there()
    {
        var (items, stash) = (Items(), Stash());
        stash.Place(Currency, 1, 0, new InventoryItem(1, 0, 0, Scrolls(5)), Shapes, out _);
        var scrolls = items.Add(Main, 3, 2, Scrolls(12));

        var moved = ItemMoves.InventoryToStash(stash, items, Currency, scrolls.Id, Shapes, out _)!;

        Assert.Equal(17, moved.TabChanged.Single().Item.StackSize);
        Assert.Null(moved.LeftInInventory);
        var (again, stashAgain) = Reload();
        Assert.Empty(again.Items(Main));
        Assert.Equal([(1, 0, 17)], stashAgain.Inventory(Currency).Items.Select(i => ((int)i.X, (int)i.Y, (int)i.Item.StackSize)));
    }

}
