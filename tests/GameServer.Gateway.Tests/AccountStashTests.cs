using GameServer.Core.Protocol.Instance;
using GameServer.Core.Saves;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class AccountStashTests : IDisposable
{
    // The test items' types (Wand, Orbs, the 2 x 2 helmet), so a stash holding them loads again.
    readonly string _folder = TestSaves.Catalog(new(0xd9b1a12f, "Wand", 1, 3), new(0x1234abcd, "Orb", StackSize: 40), new(0x1234, "Helmet", 2, 2));
    readonly string _account = $"Acc{Guid.NewGuid():N}"[..16];

    public AccountStashTests() => TestSaves.Ensure();

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    AccountStash Open() => AccountStash.For(_account, new ItemCatalog(_folder));

    // A restart of the Gateway: the stash is loaded again from the database.
    void Restart() => AccountStash.Forget(_account);

    [Fact]
    public void Each_league_has_its_own_stash_and_gold_and_standard_keeps_the_accounts()
    {
        Assert.Equal(_account, AccountStash.Key(_account, "Standard"));
        Assert.Equal(_account, AccountStash.Key(_account, null));
        var catalog = new ItemCatalog(_folder);
        var standard = AccountStash.For(AccountStash.Key(_account, "Standard"), catalog);
        var allflame = AccountStash.For(AccountStash.Key(_account, "Allflame"), catalog);
        var hardcore = AccountStash.For(AccountStash.Key(_account, "Hardcore Allflame"), catalog);
        standard.AddGold(100);
        allflame.AddGold(7);

        Assert.Equal(100u, standard.Gold);
        Assert.Equal(7u, allflame.Gold);
        Assert.Equal(0u, hardcore.Gold);
        AccountStash.Forget(AccountStash.Key(_account, "Allflame"));
        Assert.Equal(7u, AccountStash.For(AccountStash.Key(_account, "Allflame"), catalog).Gold);
    }

    static Item Wand() => new(0xd9b1a12f, ["Base"], 0x40a1);

    // Every test item is 1 × 3, like a wand.
    static PlayerInventories.Shape? Shape(Item _) => new(1, 3, "Wand");

    static Item Orbs(int n) => new(0x1234abcd, ["Base", "Usable", "Stack"], 0x1111) { StackSize = (ushort)n };
    static PlayerInventories.Shape? Orb(Item _) => new(1, 1, "StackableCurrency", 40);

}
