using GameServer.Core.Accounts;
using GameServer.Core.Saves;
using GameServer.Storage;

namespace GameServer.Core.Tests;

// Accounts and characters in the save database: the same file as the character saves, so deleting a character and
// setting its save aside are one transaction.
public class SqliteAccountStoreTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"accountdb-{Guid.NewGuid():N}");
    string DbPath => Path.Combine(_folder, "saves.db");

    public SqliteAccountStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    static Account NewAccount(string name) => new(Guid.NewGuid(), name, "hash", "salt", DateTimeOffset.UtcNow);
    static Character NewCharacter(Account a, string name, int minutesAgo = 0) => new(Guid.NewGuid(), a.Id, name, CharacterClass.Witch, 1,
        "Standard", "1_1_1", DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), DateTimeOffset.UtcNow);

    [Fact]
    public void Accounts_and_characters_are_kept_and_found_by_name_or_id()
    {
        var a = NewAccount("Sepehr");
        var older = NewCharacter(a, "Seppy", minutesAgo: 10);
        var newer = NewCharacter(a, "SepehrWitch");
        using (var db = new SaveDatabase(DbPath))
        {
            var store = new SqliteAccountStore(db);
            store.AddAccount(a);
            store.AddCharacter(newer);
            store.AddCharacter(older);
        }

        using var again = new SaveDatabase(DbPath);
        var reloaded = new SqliteAccountStore(again);
        Assert.Equal(a, reloaded.FindAccount("sepehr"));
        Assert.Equal(a, reloaded.GetAccount(a.Id));
        Assert.Equal(["Seppy", "SepehrWitch"], reloaded.CharactersOf(a.Id).Select(c => c.Name));   // oldest first
        Assert.Equal(newer, reloaded.FindCharacter("SEPEHRWITCH"));
        Assert.Null(reloaded.FindAccount("nobody"));
    }

    [Fact]
    public void The_account_list_has_every_account_by_name()
    {
        using var db = new SaveDatabase(DbPath);
        var store = new SqliteAccountStore(db);
        Assert.Empty(store.Accounts());
        foreach (string name in new[] { "zed", "Alice", "bob" }) store.AddAccount(NewAccount(name));

        Assert.Equal(["Alice", "bob", "zed"], store.Accounts().Select(a => a.Name));
    }

    [Fact]
    public void Updates_change_the_record_and_never_add_one()
    {
        using var db = new SaveDatabase(DbPath);
        var store = new SqliteAccountStore(db);
        var a = NewAccount("Sepehr");
        store.AddAccount(a);
        var c = NewCharacter(a, "Seppy");
        store.AddCharacter(c);

        store.UpdateCharacter(c with { Level = 37, LastTown = "1_2_town" });
        store.UpdateAccount(a with { PasswordHash = "new" });
        store.UpdateCharacter(NewCharacter(a, "Ghost"));   // not there: nothing happens

        Assert.Equal((37, "1_2_town"), (store.GetCharacter(c.Id)!.Level, store.GetCharacter(c.Id)!.LastTown));
        Assert.Equal("new", store.GetAccount(a.Id)!.PasswordHash);
        Assert.Null(store.FindCharacter("Ghost"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => store.AddCharacter(NewCharacter(a, "seppy")));   // names are unique
    }

    [Fact]
    public void Deleting_a_character_sets_its_save_aside_in_the_same_transaction()
    {
        using var db = new SaveDatabase(DbPath);
        var store = new SqliteAccountStore(db);
        var a = NewAccount("Sepehr");
        store.AddAccount(a);
        var c = NewCharacter(a, "Seppy");
        store.AddCharacter(c);
        db.Put(SaveDatabase.Table.Characters, "Seppy", "{\"Inventories\":{}}");

        store.DeleteCharacter(c.Id);

        Assert.Null(store.GetCharacter(c.Id));
        Assert.Null(db.Get(SaveDatabase.Table.Characters, "Seppy"));
        Assert.False(db.SetAside("Seppy"));   // already set aside

        // A failure inside the deletion keeps both.
        var d = NewCharacter(a, "Other");
        store.AddCharacter(d);
        db.Put(SaveDatabase.Table.Characters, "Other", "{\"Inventories\":{}}");
        Assert.Throws<IOException>(() => db.Atomically(() =>
        {
            store.DeleteCharacter(d.Id);
            throw new IOException("disk full");
        }));
        Assert.NotNull(store.GetCharacter(d.Id));
        Assert.NotNull(db.Get(SaveDatabase.Table.Characters, "Other"));
    }

    [Fact]
    public void The_json_account_store_is_imported_once_and_kept_renamed()
    {
        string json = Path.Combine(_folder, "accounts.json");
        var a = NewAccount("Sepehr");
        var old = new JsonAccountStore(json);
        old.AddAccount(a);
        old.AddCharacter(NewCharacter(a, "Seppy") with { Level = 12, LastTown = "1_1_town" });
        using var db = new SaveDatabase(DbPath);

        Assert.Equal(2, SqliteAccountStore.ImportJson(db, json));

        var store = new SqliteAccountStore(db);
        Assert.Equal((12, "1_1_town"), (store.FindCharacter("Seppy")!.Level, store.FindCharacter("Seppy")!.LastTown));
        Assert.True(File.Exists(json + ".imported") && !File.Exists(json));
        new JsonAccountStore(json).AddAccount(NewAccount("Later"));   // a file that turns up later is never read
        Assert.Equal(0, SqliteAccountStore.ImportJson(db, json));
        Assert.Null(store.FindAccount("Later"));
    }

    [Theory]
    [InlineData("{ not json")]
    public void An_unreadable_json_account_store_stops_the_import_and_stays_as_it_is(string content)
    {
        string json = Path.Combine(_folder, "accounts.json");
        File.WriteAllText(json, content);
        using var db = new SaveDatabase(DbPath);

        Assert.Throws<UnreadableSaveException>(() => SqliteAccountStore.ImportJson(db, json));

        Assert.Equal(content, File.ReadAllText(json));
        Assert.Null(db.GetMeta("accounts_imported"));   // tried again at the next start
    }

    [Fact]
    public void No_json_account_store_imports_nothing_and_is_not_looked_for_again()
    {
        using var db = new SaveDatabase(DbPath);
        Assert.Equal(0, SqliteAccountStore.ImportJson(db, Path.Combine(_folder, "accounts.json")));
        Assert.NotNull(db.GetMeta("accounts_imported"));
    }
}
