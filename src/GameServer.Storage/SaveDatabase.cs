using GameServer.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace GameServer.Storage;

/// <summary>
/// The saves of player progress in one SQLite file: one row per character and per account stash, the save as JSON.
/// Write-ahead log with synchronous=FULL: a committed transaction survives a crash or a power cut, and one that did not
/// commit leaves nothing behind. Every write runs in a transaction (<see cref="Atomically{T}"/>), so changes that belong
/// together (an item leaving the stash and entering the inventory) are saved together or not at all.
/// <para>Several processes may open the file (the Gateway saves, Auth sets deleted characters aside); a write waits for
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

    public string Path { get; }

    /// <summary>data/saves.db, or <c>Game_SAVES_DB</c>.</summary>
    public static string DefaultPath => Environment.GetEnvironmentVariable("Game_SAVES_DB") ?? System.IO.Path.Combine("data", "saves.db");

    public SaveDatabase(string path)
    {
        Path = path;
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        // No pooling: closing lets go of the file (Auth opens it for one deletion, tests delete it).
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        try
        {
            if (Scalar("PRAGMA journal_mode=WAL") is not "wal") throw new IOException($"{path}: SQLite's write-ahead log is not available here");
            Execute("PRAGMA synchronous=FULL");
            Execute("PRAGMA busy_timeout=10000");
            Execute("""
                CREATE TABLE IF NOT EXISTS characters (name TEXT PRIMARY KEY COLLATE NOCASE, save TEXT NOT NULL) STRICT;
                CREATE TABLE IF NOT EXISTS stashes (account TEXT PRIMARY KEY COLLATE NOCASE, save TEXT NOT NULL) STRICT;
                CREATE TABLE IF NOT EXISTS stash_tabs (account TEXT NOT NULL COLLATE NOCASE, tab INTEGER NOT NULL, items TEXT NOT NULL,
                    PRIMARY KEY (account, tab)) STRICT;
                CREATE TABLE IF NOT EXISTS deleted_characters (name TEXT NOT NULL, deleted_utc TEXT NOT NULL, save TEXT NOT NULL) STRICT;
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
                CREATE TABLE IF NOT EXISTS accounts (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, account TEXT NOT NULL) STRICT;
                CREATE TABLE IF NOT EXISTS account_characters (id TEXT PRIMARY KEY, account_id TEXT NOT NULL, name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    created_utc TEXT NOT NULL, character TEXT NOT NULL) STRICT;
                """);
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

    /// <summary>The save stored under <paramref name="key"/>, or null when there is none.</summary>
    public string? Get(Table table, string key)
    {
        lock (_gate)
        {
            using var c = Command($"SELECT save FROM {Name(table)} WHERE {KeyColumn(table)} = $key", ("$key", key));
            return c.ExecuteScalar() as string;
        }
    }

    /// <summary>Stores <paramref name="save"/> under <paramref name="key"/>, replacing what was there.</summary>
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

    /// <summary>Moves a deleted character's save to deleted_characters, so a new character of that name starts over and
    /// the old one can still be put back by hand. False when it had no save.</summary>
    public bool SetAside(string character) => Atomically(() =>
    {
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

    /// <summary>Runs a read under the connection's lock (inside a transaction, it sees that transaction's writes).</summary>
    internal T Read<T>(Func<T> read)
    {
        lock (_gate) return read();
    }

    internal SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
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
        lock (_gate) _db.Dispose();
    }
}
