using GameServer.Core.Accounts;
using GameServer.Core.Saves;
using GameServer.Storage;

namespace GameServer.Core.Tests;

// data/accounts.json holds every account and character: an unreadable file must stop Auth, never start it empty.
public class JsonAccountStoreTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"accounts-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        foreach (var f in new[] { _path, _path + ".bak", _path + ".tmp" })
            if (File.Exists(f)) File.Delete(f);
    }

    static Account NewAccount(string name) => new(Guid.NewGuid(), name, "hash", "salt", DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("{ not json")]
    public void An_unreadable_account_store_is_refused_and_left_as_it_is(string content)
    {
        File.WriteAllText(_path, content);

        Assert.Throws<UnreadableSaveException>(() => new JsonAccountStore(_path));
        Assert.Equal(content, File.ReadAllText(_path));
    }

    [Fact]
    public void A_change_while_the_account_store_is_briefly_held_is_saved()
    {
        new JsonAccountStore(_path).AddAccount(NewAccount("first"));
        var store = new JsonAccountStore(_path);

        // A reader that doesn't share delete, like an antivirus scan: replacing the file is refused while it reads.
        using (FileHold.For(_path, FileShare.ReadWrite, 300))
            store.AddAccount(NewAccount("second"));

        var reloaded = new JsonAccountStore(_path);
        Assert.NotNull(reloaded.FindAccount("first"));
        Assert.NotNull(reloaded.FindAccount("second"));
    }
}
