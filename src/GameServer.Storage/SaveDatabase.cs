using GameServer.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace GameServer.Storage;

/// <summary>
/// The player databases (Agents/docs/dev/databases.md): <c>characters.db</c> (character saves, stashes, the login
/// character list, friends) with <c>auth.db</c> (accounts) attached as <c>auth</c>, so a table of either is named
/// without its database. Both are built and updated from <c>sql/</c> (<see cref="SqlUpdater"/>) when opened.
/// Write-ahead log with synchronous=FULL: a committed transaction survives a crash or a power cut, and one that did not
/// commit leaves nothing behind. Every write runs in a transaction (<see cref="Atomically{T}"/>), so changes that belong
/// together (an item leaving the stash and entering the inventory) are saved together or not at all. The rows written
/// together (a deleted character and its save set aside) are in one file; a transaction across both files is atomic in
/// each of them only.
/// <para>Several processes may open the files (the Gateway saves, Auth sets deleted characters aside); a write waits for
/// another process's transaction to end.</para>
/// </summary>
public sealed class SaveDatabase : IDisposable
{
    public enum Table { Characters, Stashes }

    readonly SqliteConnection _db;
    // Every use of the connection is under this lock, and a transaction holds it from begin to commit: nothing else
    // writes in between. Reentrant, so a transaction can run inside another (it joins it).
    readonly Lock _gate = new();
    SqliteTransaction? _tx;
    List<Action>? _undo;
    long _ended;
    SqliteCommand? _dataVersion;

    /// <summary>The characters database.</summary>
    public string Path { get; }

    public string AuthPath { get; }

    /// <summary>data/characters.db, or <c>Game_CHARACTERS_DB</c>.</summary>
    public static string DefaultPath => Environment.GetEnvironmentVariable("Game_CHARACTERS_DB") ?? System.IO.Path.Combine("data", "characters.db");

    /// <summary>data/auth.db, or <c>Game_AUTH_DB</c>.</summary>
    public static string DefaultAuthPath => Environment.GetEnvironmentVariable("Game_AUTH_DB") ?? AuthPathFor(DefaultPath);

    /// <summary>The accounts database that goes with a characters database: auth.db beside a characters.db, else
    /// &lt;name&gt;-auth.db (a copied or scratch database keeps its accounts beside it).</summary>
    public static string AuthPathFor(string charactersPath)
    {
        string folder = System.IO.Path.GetDirectoryName(charactersPath) ?? "";
        string name = System.IO.Path.GetFileNameWithoutExtension(charactersPath);
        return System.IO.Path.Combine(folder, name.Equals("characters", StringComparison.OrdinalIgnoreCase) ? "auth.db" : name + "-auth.db");
    }

    /// <summary>The database from before the split (data/saves.db), migrated when a server first opens the default player
    /// databases (<see cref="SavesMigration.MigrateIfNeeded"/>).</summary>
    public static string LegacyPath => System.IO.Path.Combine("data", "saves.db");

    public SaveDatabase(string path, string? authPath = null)
    {
        Path = path;
        AuthPath = authPath ?? (path == DefaultPath ? DefaultAuthPath : AuthPathFor(path));
        SavesMigration.MigrateIfNeeded(path);
        SqlUpdater.Update(AuthPath, "auth");
        _db = SqlUpdater.Open(path);
        try
        {
            SqlUpdater.Update(_db, "characters");
            using (var attach = Command("ATTACH DATABASE $path AS auth", ("$path", AuthPath))) attach.ExecuteNonQuery();
            if (Scalar("PRAGMA auth.journal_mode=WAL") is not "wal") throw new IOException($"{AuthPath}: SQLite's write-ahead log is not available here");
            Execute("PRAGMA auth.synchronous=FULL");
        }
        catch
        {
            _db.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction: every write it makes is saved when it returns, and none when it
    /// throws (the exception goes on, after the <see cref="OnRollback"/> actions ran). Inside another transaction it joins
    /// that one, and is saved or undone with it. Keep the work synchronous and short: other writers wait for it.
    /// </summary>
    public T Atomically<T>(Func<T> work)
    {
        lock (_gate)
        {
            if (_tx is not null) return work();
            _tx = _db.BeginTransaction();
            _undo = [];
            try
            {
                T result = work();
                _tx.Commit();
                return result;
            }
            catch
            {
                Undo();
                throw;
            }
            finally
            {
                _tx.Dispose();
                _tx = null;
                _undo = null;
                _ended++;
            }
        }
    }

    /// <summary>
    /// A value that is the same only while nothing in the characters database can have changed: this connection has ended
    /// no transaction (every write is in one) and no other connection has committed (SQLite's data_version, which also
    /// sees Auth's process). What was read under one value may be kept and used again while the value stays the same.
    /// Null inside a transaction, whose reads see its own unfinished writes: read the rows then.
    /// </summary>
    public (long Own, long Others)? Unchanged
    {
        get
        {
            lock (_gate)
            {
                if (_tx is not null) return null;
                _dataVersion ??= Command("PRAGMA main.data_version");
                return (_ended, Convert.ToInt64(_dataVersion.ExecuteScalar()));
            }
        }
    }

    public void Atomically(Action work) => Atomically(() => { work(); return 0; });

    /// <summary>Within a transaction: <paramref name="undo"/> runs if it is undone, so what was changed in memory along
    /// with the rows can be put back (or dropped, to be loaded again).</summary>
    public void OnRollback(Action undo)
    {
        lock (_gate) (_undo ?? throw new InvalidOperationException("OnRollback outside a transaction")).Add(undo);
    }

    void Undo()
    {
        // A commit that failed may already have rolled back on its own.
        try { _tx!.Rollback(); } catch (Exception e) when (e is SqliteException or InvalidOperationException) { }
        foreach (var undo in Enumerable.Reverse(_undo!))
            try { undo(); }
            catch (Exception e) { Console.WriteLine($"[saves] undoing a failed save: {ServerLog.Describe(e)}"); }
    }

    /// <summary>The meta key set once the JSON saves were moved into the save tables (the Gateway's SaveRows.Migrate).</summary>
    public const string RelationalKey = "relational_saves";

    /// <summary>Whether the JSON saves were moved into the save tables (character_saves, stash_saves, items ...).</summary>
    public bool Relational => GetMeta(RelationalKey) is not null;

    /// <summary>The JSON save stored under <paramref name="key"/> in the tables from before the save tables (characters,
    /// stashes), or null when there is none. Only the import of older saves and the move into the save tables use them.</summary>
    public string? Get(Table table, string key)
    {
        lock (_gate)
        {
            using var c = Command($"SELECT save FROM {Name(table)} WHERE {KeyColumn(table)} = $key", ("$key", key));
            return c.ExecuteScalar() as string;
        }
    }

    /// <summary>Stores a JSON <paramref name="save"/> under <paramref name="key"/> in the tables from before the save
    /// tables, replacing what was there (the import of older saves).</summary>
    public void Put(Table table, string key, string save) => Atomically(() =>
    {
        using var c = Command($"INSERT INTO {Name(table)} ({KeyColumn(table)}, save) VALUES ($key, $save) " +
                              $"ON CONFLICT({KeyColumn(table)}) DO UPDATE SET save = excluded.save", ("$key", key), ("$save", save));
        c.ExecuteNonQuery();
    });

    /// <summary>The items of one stash tab (a stash's row holds its header: tabs, gold, last tab), or null.</summary>
    public string? GetStashTab(string account, int tab)
    {
        lock (_gate)
        {
            using var c = Command("SELECT items FROM stash_tabs WHERE account = $account AND tab = $tab", ("$account", account), ("$tab", tab));
            return c.ExecuteScalar() as string;
        }
    }

    /// <summary>Stores one stash tab's items, replacing what was there: a change to one tab writes only that tab.</summary>
    public void PutStashTab(string account, int tab, string items) => Atomically(() =>
    {
        using var c = Command("INSERT INTO stash_tabs (account, tab, items) VALUES ($account, $tab, $items) " +
                              "ON CONFLICT(account, tab) DO UPDATE SET items = excluded.items", ("$account", account), ("$tab", tab), ("$items", items));
        c.ExecuteNonQuery();
    });

    /// <summary>Sets a deleted character's save aside (character_saves.deleted_utc), so a new character of that name starts
    /// over and the old one can still be put back by hand. Before the saves were moved into the save tables, the JSON save
    /// goes to deleted_characters instead. False when it had no save.</summary>
    public bool SetAside(string character) => Atomically(() =>
    {
        using (var mark = Command("UPDATE character_saves SET deleted_utc = $at WHERE name = $name AND deleted_utc IS NULL",
                   ("$name", character), ("$at", DateTime.UtcNow.ToString("O"))))
            if (mark.ExecuteNonQuery() > 0) return true;
        if (Relational) return false;
        using var copy = Command("INSERT INTO deleted_characters (name, deleted_utc, save) SELECT name, $at, save FROM characters WHERE name = $name",
            ("$name", character), ("$at", DateTime.UtcNow.ToString("O")));
        if (copy.ExecuteNonQuery() == 0) return false;
        using var delete = Command("DELETE FROM characters WHERE name = $name", ("$name", character));
        delete.ExecuteNonQuery();
        return true;
    });

    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var c = Command("SELECT value FROM meta WHERE key = $key", ("$key", key));
            return c.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value) => Atomically(() =>
    {
        using var c = Command("INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));
        c.ExecuteNonQuery();
    });

    // Table and column names come from the enum only, never from callers' text.
    static string Name(Table t) => t switch { Table.Characters => "characters", Table.Stashes => "stashes", _ => throw new ArgumentOutOfRangeException(nameof(t)) };
    static string KeyColumn(Table t) => t == Table.Characters ? "name" : "account";

    /// <summary>A text column of a character's save (a column of character_saves, or a JSON property before the move into
    /// the save tables), or null: Auth's reads of what the Gateway saved.</summary>
    public string? CharacterText(string character, string column, string property) => Read(() =>
    {
        if (column is not ("selected_hideout")) throw new ArgumentException($"no text column {column}", nameof(column));
        using (var c = Command($"SELECT {column}, 1 FROM character_saves WHERE name = $name AND deleted_utc IS NULL", ("$name", character)))
        using (var r = c.ExecuteReader())
            if (r.Read()) return r.IsDBNull(0) ? null : r.GetString(0);
        if (Relational || Get(Table.Characters, character) is not { } json) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() : null;
    });

    /// <summary>The id of a character's main ascendancy (Ascendancy.dat Id), or null when it has none.</summary>
    public string? Ascendancy(string character) => Read(() =>
    {
        using (var c = Command("SELECT a.ascendancy, 1 FROM character_saves s LEFT JOIN character_ascendancies a ON a.save = s.id " +
                               "WHERE s.name = $name AND s.deleted_utc IS NULL", ("$name", character)))
        using (var r = c.ExecuteReader())
            if (r.Read()) return r.IsDBNull(0) ? null : r.GetString(0);
        if (Relational || Get(Table.Characters, character) is not { } json) return null;
        using var save = System.Text.Json.JsonDocument.Parse(json);
        return save.RootElement.TryGetProperty("MainAscendancy", out var progress) && progress.ValueKind == System.Text.Json.JsonValueKind.Object
            && progress.TryGetProperty("Selection", out var selection) && selection.ValueKind == System.Text.Json.JsonValueKind.Object
            && selection.TryGetProperty("Id", out var id) ? id.GetString() : null;
    });

    /// <summary>Runs a read under the connection's lock (inside a transaction, it sees that transaction's writes).</summary>
    public T Read<T>(Func<T> read)
    {
        lock (_gate) return read();
    }

    /// <summary>A command on the connection, in the current transaction. Use it under <see cref="Read{T}"/> or
    /// <see cref="Atomically{T}"/>, which hold the connection's lock.</summary>
    public SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        c.Transaction = _tx;
        foreach (var (name, value) in parameters) c.Parameters.AddWithValue(name, value);
        return c;
    }

    void Execute(string sql) { using var c = Command(sql); c.ExecuteNonQuery(); }

    object? Scalar(string sql) { using var c = Command(sql); return c.ExecuteScalar(); }

    public void Dispose()
    {
        lock (_gate)
        {
            _dataVersion?.Dispose();
            _db.Dispose();
        }
    }
}
