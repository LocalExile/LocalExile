using Microsoft.Data.Sqlite;

namespace GameServer.Core;

/// <summary>
/// The client's tables, read from <c>data/client.db</c> (Agents/docs/dev/databases.md): the database the servers build from
/// <c>sql/base/client/</c> and <c>sql/updates/client/</c> (+ <c>client_fixes/</c>). A table is read by its name with
/// <see cref="Table"/>, as rows (<see cref="ClientTable"/>): the client's own tables (PascalCase) and the generated ones
/// the extract-data.ps1 generators make (snake_case: area_topologies, object_types, stat_descriptions, ...;
/// scripts/dev/generated_tables.py).
/// </summary>
public static class ClientTables
{
    /// <summary>data/client.db, or <c>Game_CLIENT_DB</c>.</summary>
    public static string DefaultPath => Environment.GetEnvironmentVariable("Game_CLIENT_DB") ?? Path.Combine("data", "client.db");

    /// <summary>When client.db was last written (a cache key for a parse of one of its tables).</summary>
    public static DateTime Written => File.Exists(DefaultPath) ? File.GetLastWriteTimeUtc(DefaultPath) : default;

    /// <summary>Identity for derived immutable caches: this flow's override, otherwise the database path and revision.</summary>
    public static object CacheKey => (object?)Override.Value ?? (DefaultPath, Written);

    /// <summary>Whether client.db (or the tables a test gave <see cref="Use"/>) has the table.</summary>
    public static bool Has(string table)
    {
        if (Override.Value is { } tables) return tables.ContainsKey(table);
        if (!File.Exists(DefaultPath)) return false;
        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $t";
        c.Parameters.AddWithValue("$t", table);
        return c.ExecuteScalar() is not null;
    }

    /// <summary>The table's rows. Read from client.db once per version of the file and shared: a table read again within
    /// a few seconds of its last use (the catalogues load in bursts) is not read again.</summary>
    public static ClientTable Table(string table)
    {
        if (Override.Value is { } tables)
            return tables.TryGetValue(table, out var given) ? given
                : throw new FileNotFoundException($"the test gave no client table {table}", table);
        DateTime written = Written;
        lock (Tables)
            if (Tables.TryGetValue(table, out var recent) && recent.Written == written)
            {
                Tables[table] = recent with { Used = DateTime.UtcNow };
                return recent.Table;
            }
        if (!Has(table))
            throw new FileNotFoundException($"the client table {table} is not in {DefaultPath} (the servers build it from sql/ at startup)", table);
        ClientTable read;
        using (var db = Open()) read = ClientTable.Read(db, table);
        lock (Tables)
        {
            Tables[table] = (read, written, DateTime.UtcNow);
            Sweeper ??= new Timer(_ => Sweep(), null, Keep, Keep);
        }
        return read;
    }

    /// <summary>The table, or null when client.db does not have it.</summary>
    public static ClientTable? TableOrNull(string table) => Has(table) ? Table(table) : null;

    /// <summary>The table, or no rows when client.db does not have it.</summary>
    public static ClientTable TableOrEmpty(string table) => TableOrNull(table) ?? ClientTable.FromRows(table, []);

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
    static readonly Dictionary<string, (ClientTable Table, DateTime Written, DateTime Used)> Tables = new(StringComparer.Ordinal);

    static readonly TimeSpan Keep = TimeSpan.FromSeconds(5);
    static Timer? Sweeper;

    static void Sweep()
    {
        lock (Tables)
        {
            foreach (string name in Tables.Where(e => DateTime.UtcNow - e.Value.Used > Keep).Select(e => e.Key).ToList()) Tables.Remove(name);
            if (Tables.Count == 0) { Sweeper?.Dispose(); Sweeper = null; }
        }
    }

    static SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DefaultPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        db.Open();
        using var timeout = db.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout=10000";
        timeout.ExecuteNonQuery();
        return db;
    }
}
