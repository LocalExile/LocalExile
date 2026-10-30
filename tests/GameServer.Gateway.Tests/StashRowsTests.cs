using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

// A stash is a small header row and one row per tab: a click in one tab writes that tab, not the whole stash.
// Saves.FailWrite is static: the classes that set it share a collection, so they never run at once.
[Collection(TestSaves.FailWriteCollection)]
public class StashRowsTests : IDisposable
{
    const uint WandType = 0xd9b1a12f;
    readonly string _folder = TestSaves.Catalog(new TestSaves.Type(WandType, "Wand", 1, 3));
    readonly string _account = $"Rows{Guid.NewGuid():N}"[..16];

    public StashRowsTests() => TestSaves.Ensure();

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    AccountStash Open() => AccountStash.For(_account, new ItemCatalog(_folder));
    static Item Wand() => new(WandType, ["Base"], 0x40a1);
    static PlayerInventories.Shape? Shape(Item _) => new(1, 3, "Wand");

    [Fact]
    public void A_change_in_one_tab_writes_that_tab_and_the_header_only()
    {
        var stash = Open();
        stash.Place(1, 0, 0, new InventoryItem(1, 0, 0, Wand()), Shape, out _);
        var before = Enumerable.Range(0, stash.Count).Select(t => Saves.Db.GetStashTab(_account, t)).ToList();
        int writes = 0;
        Saves.FailWrite = k => { if (k.Equals(_account, StringComparison.OrdinalIgnoreCase)) writes++; return false; };
        try { stash.Place(3, 2, 0, new InventoryItem(2, 0, 0, Wand()), Shape, out _); }
        finally { Saves.FailWrite = null; }

        Assert.Equal(2, writes);   // the header and tab 3
        for (int t = 0; t < stash.Count; t++)
            if (t != 3) Assert.Equal(before[t], Saves.Db.GetStashTab(_account, t));
        Assert.DoesNotContain($"{WandType}", Saves.Get(SaveDatabase.Table.Stashes, _account));   // no items in the header
    }

}
