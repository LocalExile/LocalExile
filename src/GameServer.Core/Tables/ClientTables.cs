using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameServer.Core;

/// <summary>
/// The client's tables, read from <c>data/client.db</c> (Agents/docs/dev/databases.md): the database the servers build from
/// <c>sql/base/client/</c> and <c>sql/updates/client/</c> (+ <c>client_fixes/</c>). A table is read by its name with
/// <see cref="Table"/>, as rows (<see cref="ClientTable"/>). The files the extract-data.ps1 generators write that the
/// servers read (data/area-transitions.json, data/stat-descriptions.txt, ...: client_sql.py FILES) are in its
/// <c>client_files</c> table, one row per entry, and come back as the file through the path reads below; any other path
/// is read from disk.
/// </summary>
public static class ClientTables
{
    /// <summary>data/client.db, or <c>Game_CLIENT_DB</c>.</summary>
    public static string DefaultPath => Environment.GetEnvironmentVariable("Game_CLIENT_DB") ?? Path.Combine("data", "client.db");

    static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true };

    // A path in data/tables/: the tables are read by name, so a path there is a reader that was not moved.
    static bool TryTable(string path, out string table)
    {
        table = "";
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is null || !string.Equals(folder, Path.GetFullPath(Path.Combine("data", "tables")), StringComparison.OrdinalIgnoreCase))
            return false;
        table = Path.GetFileNameWithoutExtension(path);
        return true;
    }

    /// <summary>The generated file a path names: <c>data/&lt;file&gt;</c> under the working directory, kept in client.db.</summary>
    public static bool TryFile(string path, out string file)
    {
        file = Path.GetFileName(path);
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        return folder is not null && string.Equals(folder, Path.GetFullPath("data"), StringComparison.OrdinalIgnoreCase)
            && Files().Contains(file);
    }

    static string NoTablePath(string path) => TryTable(path, out string table)
        ? throw new ArgumentException($"{path}: read the client table by name, ClientTables.Table(\"{table}\")", nameof(path)) : path;

    /// <summary>File.Exists for a generated file path (whether client.db has it) or another file.</summary>
    public static bool Exists(string path) => TryFile(NoTablePath(path), out _) || File.Exists(path);

    public static byte[] ReadAllBytes(string path) =>
        TryFile(NoTablePath(path), out string file) ? FileBytes(file)! : File.ReadAllBytes(path);

    public static string ReadAllText(string path) =>
        TryFile(NoTablePath(path), out _) ? Encoding.UTF8.GetString(ReadAllBytes(path)) : File.ReadAllText(path);

    public static IEnumerable<string> ReadLines(string path) =>
        TryFile(NoTablePath(path), out _) ? Lines(ReadAllText(path)) : File.ReadLines(path);

    public static Stream OpenRead(string path) =>
        TryFile(NoTablePath(path), out _) ? new MemoryStream(ReadAllBytes(path), writable: false) : File.OpenRead(path);

    static IEnumerable<string> Lines(string text)
    {
        using var reader = new StringReader(text);
        for (string? line; (line = reader.ReadLine()) is not null;) yield return line;
    }

    /// <summary>A generated file's contents from client.db (<c>client-constants.json</c>, ...), or null when it has none.</summary>
    public static byte[]? FileBytes(string file) => Files().Contains(file) ? Cache("file:" + file, db => BuildFile(db, file)) : null;

    // The generated files in client.db, read again when the database file changes.
    static HashSet<string> Files()
    {
        DateTime written = Written;
        lock (FileNamesLock)
        {
            if (written == FileNamesWritten) return FileNames;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (written != default)
            {
                using var db = Open();
                using var c = db.CreateCommand();
                c.CommandText = "SELECT DISTINCT file FROM client_files";
                try { using var r = c.ExecuteReader(); while (r.Read()) names.Add(r.GetString(0)); }
                catch (SqliteException) { }   // a client.db from before client_files
            }
            // Kept once it has files: the updater may still be filling a new client.db (its WAL leaves the file's time).
            if (names.Count > 0) (FileNames, FileNamesWritten) = (names, written);
            return names;
        }
    }

    static readonly object FileNamesLock = new();
    static HashSet<string> FileNames = [];
    static DateTime FileNamesWritten = DateTime.MinValue;

    /// <summary>When client.db was last written (a cache key for a parse of one of its tables).</summary>
    public static DateTime Written => File.Exists(DefaultPath) ? File.GetLastWriteTimeUtc(DefaultPath) : default;

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

    // A rebuilt table or file under its cache name: from memory, the compressed cache, or built and stored.
    static byte[] Cache(string name, Func<SqliteConnection, byte[]> build)
    {
        lock (Recent)
            if (Recent.TryGetValue(name, out var recent))
            {
                Recent[name] = (recent.Bytes, DateTime.UtcNow);
                return recent.Bytes;
            }
        byte[] bytes;
        using (var db = Open())
        {
            string version = Version(db);
            bytes = Cached(db, name, version) ?? Store(db, name, version, build(db));
        }
        lock (Recent)
        {
            Recent[name] = (bytes, DateTime.UtcNow);
            lock (Tables) Sweeper ??= new Timer(_ => Sweep(), null, Keep, Keep);
        }
        return bytes;
    }

    // A generated file from its client_files rows (client_sql.py file_rows): a text file is its blocks' text in order; a
    // JSON document is its top-level entries, a part of two keys being an entry of the top-level list or object it names.
    static byte[] BuildFile(SqliteConnection db, string file)
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT part, value FROM client_files WHERE file = $f ORDER BY ord";
        c.Parameters.AddWithValue("$f", file);
        using var r = c.ExecuteReader();
        if (file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var text = new StringBuilder();
            while (r.Read()) text.Append(JsonSerializer.Deserialize<string>(r.GetString(1)));
            return Encoding.UTF8.GetBytes(text.ToString());
        }
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            bool? rootObject = null;
            string? open = null;   // the raw key of the entry whose parts are being written
            bool openObject = false;
            while (r.Read())
            {
                using var part = JsonDocument.Parse(r.GetString(0));
                var keys = part.RootElement;
                var first = keys[0];
                bool nested = keys.GetArrayLength() == 2;
                if (rootObject is null)
                {
                    rootObject = first.ValueKind == JsonValueKind.String;
                    if (rootObject.Value) w.WriteStartObject(); else w.WriteStartArray();
                }
                if (!nested || open != first.GetRawText())
                {
                    if (open is not null) { if (openObject) w.WriteEndObject(); else w.WriteEndArray(); open = null; }
                    if (rootObject.Value) w.WritePropertyName(first.GetString()!);
                    if (nested)
                    {
                        open = first.GetRawText();
                        openObject = keys[1].ValueKind == JsonValueKind.String;
                        if (openObject) w.WriteStartObject(); else w.WriteStartArray();
                    }
                }
                if (nested && openObject) w.WritePropertyName(keys[1].GetString()!);
                w.WriteRawValue(r.GetString(1), skipInputValidation: true);
            }
            if (open is not null) { if (openObject) w.WriteEndObject(); else w.WriteEndArray(); }
            if (rootObject is true) w.WriteEndObject(); else if (rootObject is false) w.WriteEndArray();
        }
        return buffer.ToArray();
    }

    static readonly TimeSpan Keep = TimeSpan.FromSeconds(5);
    static readonly Dictionary<string, (byte[] Bytes, DateTime Used)> Recent = new(StringComparer.Ordinal);
    static Timer? Sweeper;

    static void Sweep()
    {
        bool empty;
        lock (Recent)
        {
            foreach (string name in Recent.Where(e => DateTime.UtcNow - e.Value.Used > Keep).Select(e => e.Key).ToList()) Recent.Remove(name);
            empty = Recent.Count == 0;
        }
        lock (Tables)
        {
            foreach (string name in Tables.Where(e => DateTime.UtcNow - e.Value.Used > Keep).Select(e => e.Key).ToList()) Tables.Remove(name);
            if (empty && Tables.Count == 0) { Sweeper?.Dispose(); Sweeper = null; }
        }
    }

    // What the cache is valid for: the update files applied to client.db so far.
    static string Version(SqliteConnection db)
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT count(*) || ':' || ifnull(max(applied_utc), '') FROM updates";
        try { return c.ExecuteScalar() as string ?? ""; }
        catch (SqliteException) { return ""; }
    }

    static byte[]? Cached(SqliteConnection db, string table, string version)
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT json FROM client_json_cache WHERE name = $t AND version = $v";
        c.Parameters.AddWithValue("$t", table);
        c.Parameters.AddWithValue("$v", version);
        try
        {
            if (c.ExecuteScalar() is not byte[] packed) return null;
            using var unzip = new System.IO.Compression.ZLibStream(new MemoryStream(packed), System.IO.Compression.CompressionMode.Decompress);
            var output = new MemoryStream(packed.Length * 8);
            unzip.CopyTo(output);
            return output.ToArray();
        }
        catch (SqliteException) { return null; }   // no cache table yet
    }

    static byte[] Store(SqliteConnection db, string table, string version, byte[] json)
    {
        try
        {
            var packed = new MemoryStream();
            using (var zip = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                zip.Write(json);
            using var c = db.CreateCommand();
            c.CommandText = """
                CREATE TABLE IF NOT EXISTS client_json_cache (name TEXT PRIMARY KEY, version TEXT NOT NULL, json BLOB NOT NULL);
                INSERT INTO client_json_cache (name, version, json) VALUES ($t, $v, $j)
                    ON CONFLICT(name) DO UPDATE SET version = excluded.version, json = excluded.json;
                """;
            c.Parameters.AddWithValue("$t", table);
            c.Parameters.AddWithValue("$v", version);
            c.Parameters.AddWithValue("$j", packed.ToArray());
            c.ExecuteNonQuery();
        }
        catch (SqliteException) { }   // busy (the updater holds the file): the next read builds it again
        return json;
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
