using System.Text.Json;
using GameServer.Core.Saves;
using GameServer.Storage;

/// <summary>
/// The Gateway's save database (data/saves.db, <c>Game_SAVES_DB</c>; <see cref="SaveDatabase"/>): characters
/// (<see cref="CharacterSaves"/>) and account stashes (<see cref="AccountStash"/>).
/// <para>Every change is saved at once, in a transaction. A change that touches several saves or several parts of one
/// (an item from the stash to the cursor, a purchase, a currency item used on an item) runs in one
/// <see cref="Atomically{T}"/>: all of it is saved or none. When a save fails, the objects it changed in memory are
/// dropped (<see cref="SaveDatabase.OnRollback"/>) and loaded again from the database on their next use, and the
/// exception closes the connection: memory is never ahead of the disk.</para>
/// <para>Lock order: a connection's own locks (<c>conn.Monsters</c>, <c>conn.Npcs</c>) before the database's, and the
/// database's before an inventory's or a stash's. So the work inside a transaction never takes a connection's lock.</para>
/// </summary>
static class Saves
{
    static SaveDatabase? _db;

    public static SaveDatabase Db => _db ?? throw new InvalidOperationException("the save database is not open (Saves.Open)");

    public static string DefaultPath => SaveDatabase.DefaultPath;

    public static void Open(string path)
    {
        var old = _db;
        _db = new SaveDatabase(path);
        old?.Dispose();
    }

    public static T Atomically<T>(Func<T> work) => Db.Atomically(work);

    public static void Atomically(Action work) => Db.Atomically(work);

    /// <summary>Tests: a write whose key this says yes to fails, as a full disk would.</summary>
    internal static Func<string, bool>? FailWrite;

    public static string? Get(SaveDatabase.Table table, string key) => Db.Get(table, key);

    public static void Put(SaveDatabase.Table table, string key, string save)
    {
        if (FailWrite?.Invoke(key) == true) throw new IOException($"save of {key} failed (test)");
        Db.Put(table, key, save);
    }

    public static void PutStashTab(string account, int tab, string items)
    {
        if (FailWrite?.Invoke(account) == true) throw new IOException($"save of {account}'s stash tab {tab} failed (test)");
        Db.PutStashTab(account, tab, items);
    }

    /// <summary>One stash tab's items, or null when the tab has no row (a stash saved before tabs had rows of their own
    /// keeps its items in its header). A row that can't be read throws <see cref="UnreadableSaveException"/>.</summary>
    public static T? LoadStashTab<T>(string account, int tab, JsonSerializerOptions options) where T : class
    {
        if (Db.GetStashTab(account, tab) is not { } json) return null;
        try { return JsonSerializer.Deserialize<T>(json, options) ?? throw Unreadable(SaveDatabase.Table.Stashes, $"{account} tab {tab}", null); }
        catch (JsonException e) { throw Unreadable(SaveDatabase.Table.Stashes, $"{account} tab {tab}", e); }
    }

    /// <summary>The save stored under <paramref name="key"/>, or null when there is none. A row that isn't a save (broken
    /// JSON, "null") throws <see cref="UnreadableSaveException"/> and is left as it is: starting over would write over it.</summary>
    public static T? Load<T>(SaveDatabase.Table table, string key, JsonSerializerOptions options) where T : class
    {
        if (Get(table, key) is not { } json) return null;
        T? save;
        try { save = JsonSerializer.Deserialize<T>(json, options); }
        catch (JsonException e) { throw Unreadable(table, key, e); }
        return save ?? throw Unreadable(table, key, null);
    }

    static UnreadableSaveException Unreadable(SaveDatabase.Table table, string key, Exception? e)
    {
        string message = $"the save of {key} ({table} in {Db.Path}) can't be read{(e is null ? "" : $" ({e.Message})")}; it is left as it is: " +
                         "fix it. Don't delete it: that starts it over";
        Console.WriteLine($"[saves] {message}");
        return new UnreadableSaveException(Db.Path, message, e);
    }

    /// <summary>
    /// Saves from before the database (data/saves/*.json, data/stashes/*.json) go into it once, in one transaction, and
    /// the files are renamed to *.json.imported (kept, never read again). A file that can't be read stops the import, and
    /// the Gateway: the character would otherwise start over. Returns how many were imported.
    /// </summary>
    public static int ImportJson(SaveDatabase db, string folder, JsonSerializerOptions options)
    {
        const string Done = "json_imported";
        if (db.GetMeta(Done) is not null) return 0;
        var files = Files(Path.Combine(folder, "saves"), SaveDatabase.Table.Characters)
            .Concat(Files(Path.Combine(folder, "stashes"), SaveDatabase.Table.Stashes)).ToList();
        int imported = db.Atomically(() =>
        {
            int count = 0;
            foreach (var (path, table, key) in files)
            {
                // Read as the save it is, so a broken file stops here (UnreadableSaveException) instead of being imported.
                _ = table == SaveDatabase.Table.Characters
                    ? SaveFiles.Load<CharacterSaves.Save>(path, options) as object
                    : SaveFiles.Load<AccountStash.Save>(path, options);
                if (SaveFiles.Read(path) is not { } json || db.Get(table, key) is not null) continue;
                db.Put(table, key, json);
                count++;
            }
            db.SetMeta(Done, DateTime.UtcNow.ToString("O"));
            return count;
        });
        foreach (var (path, _, _) in files)
            try { File.Move(path, path + ".imported"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"[saves] {path} imported but not renamed: {e.Message}"); }
        if (files.Count > 0) Console.WriteLine($"[saves] {imported} JSON save(s) imported into {db.Path}; the files are kept as *.json.imported");
        return imported;
    }

    static IEnumerable<(string Path, SaveDatabase.Table Table, string Key)> Files(string folder, SaveDatabase.Table table) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json").Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Order()
                .Select(f => (f, table, Path.GetFileNameWithoutExtension(f)))
            : [];
}
