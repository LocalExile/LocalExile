using System.Text.Json;
using GameServer.Core.Saves;
using GameServer.Storage;

/// <summary>
/// The Gateway's player databases (data/characters.db, <c>Game_CHARACTERS_DB</c>, with data/auth.db; <see cref="SaveDatabase"/>): characters
/// (<see cref="CharacterSaves"/>) and account stashes (<see cref="AccountStash"/>), as rows of the save tables (<see cref="SaveRows"/>).
/// <para>Every change is saved at once, in a transaction. A change that touches several saves or several parts of one
/// (an item from the stash to the cursor, a purchase, a currency item used on an item) runs in one
/// <see cref="Atomically{T}"/>: all of it is saved or none. When a save fails, the objects it changed in memory are
/// dropped (<see cref="SaveDatabase.OnRollback"/>) and loaded again from the database on their next use, and the
/// exception closes the connection: memory is never ahead of the disk.</para>
/// <para>Lock order: a connection's own locks (<c>conn.Monsters</c>, <c>conn.Npcs</c>) before the database's, and the
/// database's before an inventory's or a stash's. So the work inside a transaction never takes a connection's lock.</para>
/// </summary>
public static class Saves
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
    public static Func<string, bool>? FailWrite;

    /// <summary>A save as JSON (the record the tables hold), or null when there is none: tests compare saves with it.</summary>
    public static string? Get(SaveDatabase.Table table, string key) => table == SaveDatabase.Table.Characters
        ? LoadCharacter(key) is { } save ? JsonSerializer.Serialize(save, CharacterSaves.Opts) : null
        : LoadStash(key) is { } stash ? JsonSerializer.Serialize(stash, CharacterSaves.Opts) : null;

    /// <summary>Writes a save given as JSON (tests).</summary>
    public static void Put(SaveDatabase.Table table, string key, string json)
    {
        if (table == SaveDatabase.Table.Characters) PutCharacter(key, JsonSerializer.Deserialize<CharacterSaves.Save>(json, CharacterSaves.Opts)!);
        else PutStash(key, JsonSerializer.Deserialize<AccountStash.Save>(json, CharacterSaves.Opts)!);
    }

    /// <summary>A character's save, or null when it has none. A save that can't be read throws
    /// <see cref="UnreadableSaveException"/> and is left as it is: starting over would write over it.</summary>
    public static CharacterSaves.Save? LoadCharacter(string character) => Readable(SaveDatabase.Table.Characters, character, () => SaveRows.LoadCharacter(Db, character));

    public static void PutCharacter(string character, CharacterSaves.Save save)
    {
        if (FailWrite?.Invoke(character) == true) throw new IOException($"save of {character} failed (test)");
        SaveRows.WriteCharacter(Db, character, save);
    }

    /// <summary>A stash's header (its tabs without their items), or null when it has none.</summary>
    public static AccountStash.Save? LoadStash(string account) => Readable(SaveDatabase.Table.Stashes, account, () => SaveRows.LoadStash(Db, account));

    public static void PutStash(string account, AccountStash.Save header)
    {
        if (FailWrite?.Invoke(account) == true) throw new IOException($"save of {account}'s stash failed (test)");
        SaveRows.WriteStash(Db, account, header);
    }

    /// <summary>One stash tab's items, or null when the stash has none saved.</summary>
    public static List<CharacterSaves.SavedPlacement>? LoadStashTab(string account, int tab) =>
        Readable(SaveDatabase.Table.Stashes, $"{account} tab {tab}", () => SaveRows.LoadStashTab(Db, account, tab));

    public static void PutStashTab(string account, int tab, List<CharacterSaves.SavedPlacement> items)
    {
        if (FailWrite?.Invoke(account) == true) throw new IOException($"save of {account}'s stash tab {tab} failed (test)");
        SaveRows.WriteStashTab(Db, account, tab, items);
    }

    static T? Readable<T>(SaveDatabase.Table table, string key, Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidCastException or FormatException or OverflowException)
        {
            throw Unreadable(table, key, e);
        }
    }

    static UnreadableSaveException Unreadable(SaveDatabase.Table table, string key, Exception? e)
    {
        string message = $"the save of {key} ({table} in {Db.Path}) can't be read{(e is null ? "" : $" ({e.Message})")}; it is left as it is: " +
                         "fix it. Don't delete it: that starts it over";
        Console.WriteLine($"[saves] {message}");
        return new UnreadableSaveException(Db.Path, message, e);
    }

    /// <summary>
    /// Saves from before the database (data/saves/*.json, data/stashes/*.json) go into its JSON tables once (the move into
    /// the save tables, <see cref="SaveRows.Migrate"/>, takes them from there), in one transaction, and
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
