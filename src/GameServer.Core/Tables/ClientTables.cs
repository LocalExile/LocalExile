using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace GameServer.Core;

/// <summary>
/// The client's tables, read from <c>data/client.db</c> (Agents/docs/dev/databases.md): the database the servers build from
/// <c>sql/base/client/</c> and <c>sql/updates/client/</c> (+ <c>client_fixes/</c>) at startup, and never write while they
/// run. A table is read by its name with <see cref="Table"/>, as rows (<see cref="ClientTable"/>): the client's own tables
/// (PascalCase) and the generated ones the extract-data.ps1 generators make (snake_case: area_topologies, object_types,
/// stat_descriptions, ...; scripts/dev/generated_tables.py).
/// <para>The tables live in memory, as a game server keeps its static data (Agents/docs/dev/performance.md, "Game data in
/// memory"): the Gateway loads every one at startup (<see cref="LoadAll"/>) and closes the database; a process that does
/// not (Auth, tools, tests) loads each table the first time it is asked for, over one read-only connection it keeps. A
/// loaded table is kept for the life of the process, and <see cref="Has"/> is a lookup in the table names read once, so
/// no read while the game runs touches SQLite. A client.db rebuilt while a process runs (another server applying new
/// updates) is seen by that process's next start.</para>
/// </summary>
public static class ClientTables
{
    /// <summary>data/client.db, or <c>Game_CLIENT_DB</c> (read once: a process reads one database).</summary>
    public static string DefaultPath { get; } = Environment.GetEnvironmentVariable("Game_CLIENT_DB") ?? Path.Combine("data", "client.db");

    /// <summary>Identity for derived immutable caches: this flow's override, otherwise the loaded database.</summary>
    public static object CacheKey => (object?)Override.Value ?? (object?)Current() ?? DefaultPath;

    /// <summary>Whether client.db (or the tables a test gave <see cref="Use"/>) has the table.</summary>
    public static bool Has(string table)
    {
        if (Override.Value is { } tables) return tables.ContainsKey(table);
        return Current()?.Names.Contains(table) == true;
    }

    /// <summary>The table's rows, loaded once and kept.</summary>
    public static ClientTable Table(string table)
    {
        if (Override.Value is { } tables)
            return tables.TryGetValue(table, out var given) ? given
                : throw new FileNotFoundException($"the test gave no client table {table}", table);
        if (Current() is not { } store || !store.Names.Contains(table))
            throw new FileNotFoundException($"the client table {table} is not in {DefaultPath} (the servers build it from sql/ at startup)", table);
        return store.Get(table);
    }

    /// <summary>The table, or null when client.db does not have it.</summary>
    public static ClientTable? TableOrNull(string table) => Has(table) ? Table(table) : null;

    /// <summary>The table, or no rows when client.db does not have it.</summary>
    public static ClientTable TableOrEmpty(string table) => TableOrNull(table) ?? ClientTable.FromRows(table, []);

    /// <summary>The names of every table client.db has (empty without it).</summary>
    public static IReadOnlyCollection<string> Names => Current()?.Names ?? (IReadOnlyCollection<string>)[];

    /// <summary>What <see cref="LoadAll"/> did.</summary>
    public sealed record LoadResult(int Tables, long Rows, int Strings, TimeSpan Elapsed);

    /// <summary>Loads every table of client.db into memory, the largest first, over <paramref name="workers"/> read-only
    /// connections (default: one per core, at most 8), then closes the database: from here on no table read touches
    /// SQLite. The Gateway calls it once at startup, before anything reads a table. Tables already loaded are kept.</summary>
    public static LoadResult LoadAll(int workers = 0)
    {
        var watch = Stopwatch.StartNew();
        var store = Current() ?? throw new FileNotFoundException($"{DefaultPath} is missing (the servers build it from sql/ at startup)", DefaultPath);
        workers = workers > 0 ? workers : Math.Clamp(Environment.ProcessorCount, 1, 8);
        var order = store.RowCounts().OrderByDescending(e => e.Value).Select(e => e.Key).Where(n => !store.IsLoaded(n)).ToList();
        var queue = new ConcurrentQueue<string>(order);
        var pools = new ConcurrentBag<StringPool>();
        var failures = new ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(0, Math.Min(workers, Math.Max(1, order.Count))).Select(_ => new Thread(() =>
        {
            var pool = new StringPool();
            pools.Add(pool);
            try
            {
                using var db = OpenReadOnly(store.Path);
                while (queue.TryDequeue(out var name)) store.Put(name, ClientTable.Read(db, name, pool));
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        }, 16 << 20) { Name = "client tables load", IsBackground = true }).ToList();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        if (!failures.IsEmpty) throw new AggregateException("client tables could not be loaded", failures);
        store.CloseConnection();
        long rows = store.Names.Sum(n => (long)store.Get(n).Count);
        return new LoadResult(store.Names.Count, rows, pools.Sum(p => p.Count), watch.Elapsed);
    }

    /// <summary>Until disposed, this async flow (a test) reads these tables instead of client.db's.</summary>
    public static IDisposable Use(params ClientTable[] tables)
    {
        var previous = Override.Value;
        Override.Value = tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        return new Restore(() => Override.Value = previous);
    }

    sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    static readonly AsyncLocal<Dictionary<string, ClientTable>?> Override = new();

    /// <summary>A read-only connection to a client.db (no pooling: the caller owns it).</summary>
    internal static SqliteConnection OpenReadOnly(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        db.Open();
        using var timeout = db.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout=10000";
        timeout.ExecuteNonQuery();
        return db;
    }

    static Store? store;
    static readonly object StoreLock = new();

    /// <summary>The store of <see cref="DefaultPath"/>, made on first use; null while the file is missing (it is made
    /// once the file exists: the servers build it before they read it).</summary>
    static Store? Current()
    {
        if (Volatile.Read(ref store) is { } s) return s;
        lock (StoreLock)
        {
            if (store is not null) return store;
            if (!File.Exists(DefaultPath)) return null;
            return store = new Store(Path.GetFullPath(DefaultPath));
        }
    }

    /// <summary>One client.db: its table names (read once, when it is first used), the tables loaded so far, and the
    /// connection that loads the others one at a time.</summary>
    sealed class Store
    {
        public readonly string Path;
        public readonly HashSet<string> Names;
        readonly ConcurrentDictionary<string, ClientTable> tables = new(StringComparer.Ordinal);
        readonly object connectionLock = new();
        readonly StringPool strings = new();
        SqliteConnection? connection;

        public Store(string path)
        {
            Path = path;
            connection = OpenReadOnly(path);
            // The tables with rows (_row): the updater's own bookkeeping tables have none and are not client tables.
            var names = new List<string>();
            using (var c = connection.CreateCommand())
            {
                c.CommandText = "SELECT m.name FROM sqlite_master m WHERE m.type = 'table' " +
                    "AND EXISTS (SELECT 1 FROM pragma_table_info(m.name) WHERE name = '_row')";
                using var r = c.ExecuteReader();
                while (r.Read()) names.Add(r.GetString(0));
            }
            Names = new HashSet<string>(names, StringComparer.Ordinal);
        }

        /// <summary>Each table's row count, for loading the largest first.</summary>
        public Dictionary<string, long> RowCounts()
        {
            lock (connectionLock)
            {
                connection ??= OpenReadOnly(Path);
                var counts = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var name in Names)
                {
                    using var c = connection.CreateCommand();
                    c.CommandText = $"SELECT count(*) FROM {ClientTable.Quote(name)}";
                    counts[name] = Convert.ToInt64(c.ExecuteScalar());
                }
                return counts;
            }
        }

        public bool IsLoaded(string name) => tables.ContainsKey(name);

        public void Put(string name, ClientTable table) => tables.TryAdd(name, table);

        public ClientTable Get(string name)
        {
            if (tables.TryGetValue(name, out var table)) return table;
            lock (connectionLock)
            {
                if (tables.TryGetValue(name, out table)) return table;
                connection ??= OpenReadOnly(Path);
                return tables[name] = ClientTable.Read(connection, name, strings);
            }
        }

        public void CloseConnection()
        {
            lock (connectionLock)
            {
                connection?.Dispose();
                connection = null;
            }
        }
    }
}
