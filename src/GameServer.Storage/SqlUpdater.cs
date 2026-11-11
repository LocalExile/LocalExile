using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GameServer.Storage;

/// <summary>
/// Builds a database from the SQL in git (Agents/docs/dev/databases.md): the schema files in <c>sql/base/&lt;db&gt;/</c>,
/// then the update files of the database's folders in name order, then its pending files. Each file runs once, in a
/// transaction of its own, and is recorded in the database's <c>updates</c> table with the SHA-1 of its text: a recorded
/// file whose text changed stops the server (a fix is a new update), and so does a file that fails. A file renamed with the
/// same text (a pending update the merge moved and dated) only has its record renamed.
/// <para>Several processes may update one file at once (Auth and the Gateway both open the player databases): each file
/// is applied under SQLite's write lock and checked again there, so it runs once.</para>
/// </summary>
public static class SqlUpdater
{
    /// <summary>The databases and the folders of <c>sql/updates/</c> each one applies, merged in name order.</summary>
    static readonly Dictionary<string, string[]> UpdateFolders = new(StringComparer.Ordinal)
    {
        ["auth"] = ["auth"],
        ["characters"] = ["characters"],
        ["client"] = ["client", "client_fixes"],
        ["world"] = ["world", "ruthless"],
        ["logs"] = ["logs"],
    };

    public static IReadOnlyCollection<string> Databases => UpdateFolders.Keys;

    /// <summary>sql/ at the repository root (servers run there), or <c>Game_SQL_DIR</c>.</summary>
    public static string Root => Environment.GetEnvironmentVariable("Game_SQL_DIR") ?? "sql";

    public sealed record Script(string Name, string Path, bool Base, bool Pending);

    /// <summary>The files that build <paramref name="database"/>, in the order they apply. A name is the path under
    /// <paramref name="root"/> with forward slashes (<c>updates/world/2026_11_09_120000_x.sql</c>).</summary>
    public static IReadOnlyList<Script> Scripts(string database, string? root = null)
    {
        root ??= Root;
        if (!UpdateFolders.TryGetValue(database, out var folders)) throw new ArgumentException($"no database {database}", nameof(database));
        if (!Directory.Exists(System.IO.Path.Combine(root, "base", database)))
            throw new DirectoryNotFoundException($"{System.IO.Path.Combine(root, "base", database)} is missing: run the server from the repository root " +
                "(or set Game_SQL_DIR to the sql folder)");
        var result = Files(root, $"base/{database}").Select(f => f with { Base = true }).ToList();
        result.AddRange(folders.SelectMany(f => Files(root, $"updates/{f}"))
            .OrderBy(s => System.IO.Path.GetFileName(s.Name), StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal));
        result.AddRange(folders.SelectMany(f => Files(root, $"updates/pending_{f}")).Select(s => s with { Pending = true })
            .OrderBy(s => s.Name, StringComparer.Ordinal));
        return result;
    }

    static IEnumerable<Script> Files(string root, string folder)
    {
        string path = System.IO.Path.Combine(root, folder);
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.sql").Order(StringComparer.Ordinal)
                .Select(f => new Script($"{folder}/{System.IO.Path.GetFileName(f)}", f, false, false))
            : [];
    }

    /// <summary>The text a file's hash and run use: line ends as LF, so a checkout with CRLF hashes the same.</summary>
    public static string Text(string path) => File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");

    public static string Hash(string text) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Opens <paramref name="path"/> (creating it), the way every database here is opened: write-ahead log,
    /// synchronous=FULL, a busy timeout, and no pooling (closing lets go of the file).</summary>
    public static SqliteConnection Open(string path)
    {
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        try
        {
            if (Scalar(db, "PRAGMA journal_mode=WAL") is not "wal") throw new IOException($"{path}: SQLite's write-ahead log is not available here");
            Execute(db, "PRAGMA synchronous=FULL");
            Execute(db, "PRAGMA busy_timeout=10000");
        }
        catch
        {
            db.Dispose();
            throw;
        }
        return db;
    }

    /// <summary>Brings the database at <paramref name="path"/> up to date (<see cref="Update(SqliteConnection, string, string?, Action{string}?)"/>).</summary>
    public static int Update(string path, string database, string? root = null, Action<string>? log = null)
    {
        using var db = Open(path);
        return Update(db, database, root, log);
    }

    /// <summary>Applies every file of <paramref name="database"/> not yet recorded in the connection's main database.
    /// Returns how many ran. Throws <see cref="SqlUpdateException"/> on a failed or changed file; what ran before it
    /// stays.</summary>
    public static int Update(SqliteConnection db, string database, string? root = null, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        Execute(db, """
            CREATE TABLE IF NOT EXISTS updates (
                name TEXT PRIMARY KEY,
                hash TEXT NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('BASE', 'UPDATE', 'PENDING')),
                applied_utc TEXT NOT NULL,
                speed_ms INTEGER NOT NULL
            ) STRICT
            """);
        root ??= Root;
        var scripts = Scripts(database, root);
        var recorded = Recorded(db);
        // A large update may hold the write lock a while; another process waits for it.
        Execute(db, "PRAGMA busy_timeout=600000");
        int ran = 0;
        try
        {
            foreach (var script in scripts)
            {
                string text = Text(script.Path);
                string hash = Hash(text);
                if (recorded.TryGetValue(script.Name, out string? had))
                {
                    if (had != hash) throw Changed(database, script);
                    continue;
                }
                if (Apply(db, database, root, script, text, hash, log)) ran++;
                recorded[script.Name] = hash;
            }
        }
        finally
        {
            Execute(db, "PRAGMA busy_timeout=10000");
        }
        if (ran > 0) log($"[sql] {database}: {ran} file(s) applied");
        return ran;
    }

    static Dictionary<string, string> Recorded(SqliteConnection db)
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT name, hash FROM updates";
        using var r = c.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (r.Read()) result[r.GetString(0)] = r.GetString(1);
        return result;
    }

    // One file in one transaction, under the write lock taken at its start (BEGIN IMMEDIATE): another process that applied
    // it in the meantime is seen here, and the file is skipped.
    static bool Apply(SqliteConnection db, string database, string root, Script script, string text, string hash, Action<string> log)
    {
        using var tx = db.BeginTransaction(deferred: false);
        string? had = Lookup(db, tx, "SELECT hash FROM updates WHERE name = $v", script.Name);
        if (had is not null)
        {
            tx.Rollback();
            if (had != hash) throw Changed(database, script);
            return false;
        }
        // The same text under a new name: a pending update the merge renamed, whose file is gone. Rename the record.
        if (!script.Base && Lookup(db, tx, "SELECT name FROM updates WHERE hash = $v AND state <> 'BASE'", hash) is { } old
            && !File.Exists(System.IO.Path.Combine(root, old)) && old != script.Name)
        {
            Run(db, tx, "UPDATE updates SET name = $name, state = $state WHERE name = $old",
                ("$name", script.Name), ("$state", State(script)), ("$old", old));
            tx.Commit();
            log($"[sql] {database}: {old} is now {script.Name}");
            return false;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (text.Trim().Length > 0) Run(db, tx, text);
        }
        catch (SqliteException e)
        {
            tx.Rollback();
            throw new SqlUpdateException($"[sql] {database}: {script.Name} failed: {e.Message}. Nothing of it was applied; fix the file " +
                "(a merged one with a new update) and start again", e);
        }
        Run(db, tx, "INSERT INTO updates (name, hash, state, applied_utc, speed_ms) VALUES ($name, $hash, $state, $utc, $ms)",
            ("$name", script.Name), ("$hash", hash), ("$state", State(script)), ("$utc", DateTime.UtcNow.ToString("O")),
            ("$ms", watch.ElapsedMilliseconds));
        tx.Commit();
        if (watch.ElapsedMilliseconds >= 1000) log($"[sql] {database}: {script.Name} ({watch.ElapsedMilliseconds} ms)");
        return true;
    }

    static string State(Script s) => s.Base ? "BASE" : s.Pending ? "PENDING" : "UPDATE";

    static SqlUpdateException Changed(string database, Script script) => new(
        $"[sql] {database}: {script.Name} changed after it was applied. A merged file never changes: put it back and add a new " +
        (script.Pending ? "update. While it is still pending, delete its row from the updates table (or the database) to run it again"
            : "update with the fix"), null);

    static string? Lookup(SqliteConnection db, SqliteTransaction tx, string sql, string value)
    {
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.Parameters.AddWithValue("$v", value);
        return c.ExecuteScalar() as string;
    }

    static void Run(SqliteConnection db, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.CommandTimeout = 0;
        foreach (var (name, value) in parameters) c.Parameters.AddWithValue(name, value);
        c.ExecuteNonQuery();
    }

    internal static void Execute(SqliteConnection db, string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection db, string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        return c.ExecuteScalar();
    }
}

/// <summary>A database could not be brought up to date: the server does not start.</summary>
public sealed class SqlUpdateException(string message, Exception? inner) : Exception(message, inner);
