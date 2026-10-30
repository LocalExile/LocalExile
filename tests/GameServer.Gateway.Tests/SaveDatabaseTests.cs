using GameServer.Core.Saves;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

// The save database itself, each test on a database file of its own.
public class SaveDatabaseTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"savedb-{Guid.NewGuid():N}");
    string DbPath => Path.Combine(_folder, "saves.db");
    const SaveDatabase.Table Characters = SaveDatabase.Table.Characters;

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Committed_saves_are_there_after_reopening()
    {
        using (var db = new SaveDatabase(DbPath))
        {
            db.Put(Characters, "Anna", "{\"a\":1}");
            db.Put(SaveDatabase.Table.Stashes, "Anna", "{\"s\":2}");
            db.Put(Characters, "anna", "{\"a\":3}");   // names are case-insensitive, as the Gateway's caches
        }
        using var again = new SaveDatabase(DbPath);
        Assert.Equal("{\"a\":3}", again.Get(Characters, "ANNA"));
        Assert.Equal("{\"s\":2}", again.Get(SaveDatabase.Table.Stashes, "Anna"));
        Assert.Null(again.Get(Characters, "Bob"));
    }

    [Fact]
    public void A_throw_undoes_every_write_of_the_transaction_and_runs_the_undo_actions()
    {
        using var db = new SaveDatabase(DbPath);
        db.Put(Characters, "Anna", "before");
        var undone = new List<string>();

        Assert.Throws<IOException>(() => db.Atomically(() =>
        {
            db.Put(Characters, "Anna", "after");
            db.OnRollback(() => undone.Add("first"));
            db.Put(SaveDatabase.Table.Stashes, "Anna", "new");
            db.OnRollback(() => undone.Add("second"));
            throw new IOException("disk full");
        }));

        Assert.Equal("before", db.Get(Characters, "Anna"));
        Assert.Null(db.Get(SaveDatabase.Table.Stashes, "Anna"));
        Assert.Equal(["second", "first"], undone);
    }

    [Fact]
    public void A_transaction_inside_another_joins_it()
    {
        using var db = new SaveDatabase(DbPath);

        Assert.Throws<InvalidOperationException>(() => db.Atomically(() =>
        {
            db.Atomically(() => db.Put(Characters, "Anna", "inner"));   // returns, but is not saved on its own
            throw new InvalidOperationException("the outer one fails");
        }));
        Assert.Null(db.Get(Characters, "Anna"));

        db.Atomically(() => db.Atomically(() => db.Put(Characters, "Anna", "both")));
        Assert.Equal("both", db.Get(Characters, "Anna"));
        Assert.Throws<InvalidOperationException>(() => db.OnRollback(() => { }));   // only inside a transaction
    }

    [Fact]
    public void A_deleted_character_is_set_aside_and_a_new_one_of_that_name_starts_over()
    {
        using var db = new SaveDatabase(DbPath);
        db.Put(Characters, "Anna", "{\"old\":true}");

        Assert.True(db.SetAside("anna"));
        Assert.Null(db.Get(Characters, "Anna"));
        Assert.False(db.SetAside("Anna"));   // nothing left to set aside
    }

    [Fact]
    public void Json_saves_are_imported_once_and_their_files_kept_renamed()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "saves", "deleted"));
        Directory.CreateDirectory(Path.Combine(_folder, "stashes"));
        string anna = Path.Combine(_folder, "saves", "Anna.json"), stash = Path.Combine(_folder, "stashes", "acc.json");
        File.WriteAllText(anna, "{\"Inventories\":{},\"Experience\":525,\"Future\":1}");
        File.WriteAllText(stash, "{\"Tabs\":[],\"Gold\":12}");
        File.WriteAllText(Path.Combine(_folder, "saves", "deleted", "Old-1.json"), "{\"Inventories\":{}}");
        File.WriteAllText(anna + ".bak", "older");
        using var db = new SaveDatabase(DbPath);

        Assert.Equal(2, Saves.ImportJson(db, _folder, CharacterSaves.Opts));

        // As written, unknown fields included.
        Assert.Equal("{\"Inventories\":{},\"Experience\":525,\"Future\":1}", db.Get(Characters, "Anna"));
        Assert.Equal("{\"Tabs\":[],\"Gold\":12}", db.Get(SaveDatabase.Table.Stashes, "acc"));
        Assert.Null(db.Get(Characters, "Old-1"));   // deleted characters stay where they are
        Assert.True(File.Exists(anna + ".imported") && !File.Exists(anna));
        Assert.True(File.Exists(stash + ".imported"));

        // Never again, even when a file turns up later.
        File.WriteAllText(anna, "{\"Inventories\":{},\"Experience\":1}");
        Assert.Equal(0, Saves.ImportJson(db, _folder, CharacterSaves.Opts));
        Assert.Contains("525", db.Get(Characters, "Anna"));
    }

    [Fact]
    public void An_unreadable_json_save_stops_the_import_and_imports_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "saves"));
        File.WriteAllText(Path.Combine(_folder, "saves", "Anna.json"), "{\"Inventories\":{}}");
        File.WriteAllText(Path.Combine(_folder, "saves", "Bob.json"), "{ broken");
        using var db = new SaveDatabase(DbPath);

        Assert.Throws<UnreadableSaveException>(() => Saves.ImportJson(db, _folder, CharacterSaves.Opts));

        Assert.Null(db.Get(Characters, "Anna"));
        Assert.True(File.Exists(Path.Combine(_folder, "saves", "Anna.json")));
        // Fixed, it imports on the next start.
        File.WriteAllText(Path.Combine(_folder, "saves", "Bob.json"), "{\"Inventories\":{}}");
        Assert.Equal(2, Saves.ImportJson(db, _folder, CharacterSaves.Opts));
    }

    [Fact]
    public void The_database_is_in_write_ahead_log_mode()
    {
        using (new SaveDatabase(DbPath)) { }
        // The first 100 bytes are SQLite's header: bytes 18 and 19 are 2 in WAL mode.
        byte[] header = File.ReadAllBytes(DbPath)[..20];
        Assert.Equal((2, 2), (header[18], header[19]));
    }
}
