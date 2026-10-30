using GameServer.Core.Protocol.Instance;
using static GameServer.Gateway.Tests.StashRulesTests;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public class CurrencyWithdrawalTests : IDisposable
{
    readonly string _folder = TestSaves.Catalog(new TestSaves.Type(ScrollType, "Scroll", StackSize: 40),
        new TestSaves.Type(MapType, "Map"));
    readonly string _key = $"Withdraw{Guid.NewGuid():N}"[..24];
    const byte Main = PlayerInventories.MainInventory;

    public CurrencyWithdrawalTests() => TestSaves.Ensure();

    public void Dispose()
    {
        Saves.FailWrite = null;
        Directory.Delete(_folder, recursive: true);
    }

    ItemCatalog Catalog() => new(_folder)
    {
        Stash = StashRules.For(new() { [ScrollType] = [3] }, new() { [3] = [StashRules.Currency] },
            new() { [StashRules.Currency] = new() { [ScrollType] = 1 } }, new() { [ScrollType] = 5000 }),
    };

    (PlayerInventories Items, AccountStash Stash) Open() =>
        (PlayerInventories.For(_key, Catalog()), AccountStash.For(_key, Catalog()));

    (PlayerInventories Items, AccountStash Stash) Reload()
    {
        PlayerInventories.Forget(_key);
        AccountStash.Forget(_key);
        return Open();
    }

    static InventoryItem Deposit(AccountStash stash, int amount) =>
        stash.Place(Currency, 1, 0, new InventoryItem(1, 0, 0, Scrolls(amount)), Shapes, out _)!.Value.Placed;

    static void FillExceptFirst(PlayerInventories items)
    {
        for (byte x = 0; x < 12; x++)
            for (byte y = 0; y < 5; y++)
                if (x != 0 || y != 0) items.Add(Main, x, y, MapItem());
    }

    [Theory]
    [InlineData(25, 25, 0)]
    public void A_left_click_withdraws_at_most_one_inventory_stack(int source, int expected, int remaining)
    {
        var (items, stash) = Open();
        var inTab = Deposit(stash, source);

        var moved = ItemMoves.StashToInventory(stash, items, Currency, inTab.Id, Shapes)!;

        Assert.Equal(expected, moved.InventoryAdded.Single().Item.StackSize);
        Assert.Equal(remaining, moved.LeftInTab?.Item.StackSize ?? 0);
        var (again, stashAgain) = Reload();
        Assert.Equal(expected, again.Items(Main).Single().Item.StackSize);
        Assert.Equal(remaining, stashAgain.Inventory(Currency).Items.Sum(i => i.Item.StackSize));
        if (remaining > 0) Assert.Equal(inTab.Id, stashAgain.Inventory(Currency).Items.Single().Id);
    }

}
