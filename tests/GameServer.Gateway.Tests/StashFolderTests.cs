using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>Stash tab folders (C→S 0xd1, 0xd2): a tab's own index makes a folder, a deleted folder stays as a hidden
/// record that the next new folder reuses, and all of it survives a reload.</summary>
public class StashFolderTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    [Fact]
    public void Folders_are_made_filled_deleted_reused_and_reloaded()
    {
        var catalog = new ItemCatalog("data");
        TestEnvironment.OpenOwnSaves("stashfolder");
        string account = "StashFolder" + Guid.NewGuid().ToString("N");
        // Both messages must frame, or the inbox drops them as unknown bytes before any handler runs.
        Assert.Equal(InstanceStash.MoveTabLength, InstanceProtocol.KnownClientLength(InstanceStash.MoveTabId));
        Assert.Equal(InstanceStash.DeleteFolderLength, InstanceProtocol.KnownClientLength(InstanceStash.DeleteFolderId));
        try
        {
            var stash = AccountStash.For(account, catalog);
            int shown = stash.VisibleCount;
            InstanceStash.Tab Record(AccountStash s, int i) => s.Records(null).Single(r => r.Index == i);

            int folder = Assert.IsType<int>(stash.MoveToFolder(0, 0));
            Assert.Equal(StashRules.Folder, stash.TabType(folder));
            // As retail's record: "New Folder", premium flag, no inventory, no colour.
            Assert.Equal("New Folder", stash.NameOf(folder));
            Assert.Equal((InstanceStash.TabFlags.Premium, 0u, 0u), (Record(stash, folder).Flags, Record(stash, folder).Inventory, Record(stash, folder).U2c));
            Assert.Equal(folder, stash.MoveToFolder(1, folder));
            Assert.Null(stash.MoveToFolder(folder, folder));   // no folder in a folder
            Assert.Equal(shown + 1, stash.VisibleCount);
            Assert.True(stash.Rename(folder, "Mine"));

            Assert.Equal(ushort.MaxValue, stash.MoveToFolder(1, ushort.MaxValue));
            Assert.True(stash.DeleteFolder(folder));
            Assert.Equal(ushort.MaxValue, Record(stash, 0).Parent);
            Assert.True(Record(stash, folder).Hidden);
            Assert.Equal(shown, stash.VisibleCount);

            // The next folder takes the deleted one's record, so no later tab moves.
            Assert.Equal(folder, stash.MoveToFolder(2, 2));
            AccountStash.Forget(account);
            var reloaded = AccountStash.For(account, catalog);
            Assert.Equal(folder, Record(reloaded, 2).Parent);
            Assert.False(Record(reloaded, folder).Hidden);
            Assert.Equal(shown + 1, reloaded.VisibleCount);
            Assert.True(reloaded.DeleteFolder(folder));
            AccountStash.Forget(account);
            Assert.True(Record(AccountStash.For(account, catalog), folder).Hidden);
        }
        finally { AccountStash.Forget(account); }
    }
}
