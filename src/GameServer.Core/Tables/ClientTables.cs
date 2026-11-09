using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameServer.Core;

/// <summary>
/// The client's tables, read from <c>data/client.db</c> (Agents/docs/dev/databases.md): the database the servers build from
/// <c>sql/base/client/</c> and <c>sql/updates/client/</c> (+ <c>client_fixes/</c>). A table is still named by the path of
/// its extracted file, <c>data/tables/&lt;Table&gt;.json</c>, and comes back as that file's JSON (an array of row objects,
/// the columns in order), rebuilt from the table's rows and declared column types. The files the extract-data.ps1
/// generators write that the servers read (data/area-transitions.json, data/stat-descriptions.txt, ...: client_sql.py
/// FILES) are in its <c>client_files</c> table, one row per entry, and come back as the file. Any other path is read from
/// disk, so a reader can take a folder of its own (a test's) and still go through here.
/// </summary>
public static class ClientTables
{
    /// <summary>data/client.db, or <c>Game_CLIENT_DB</c>.</summary>
    public static string DefaultPath => Environment.GetEnvironmentVariable("Game_CLIENT_DB") ?? Path.Combine("data", "client.db");

    static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true };

    /// <summary>The table a path names: <c>data/tables/&lt;Table&gt;.json</c> under the working directory.</summary>
    public static bool TryTable(string path, out string table)
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

    /// <summary>File.Exists for a table or generated file path: whether client.db has it.</summary>
    public static bool Exists(string path) =>
        TryTable(path, out string table) ? Has(table) : TryFile(path, out _) || File.Exists(path);

    public static byte[] ReadAllBytes(string path) =>
        TryTable(path, out string table) ? Json(table, path) : TryFile(path, out string file) ? FileBytes(file)! : File.ReadAllBytes(path);

    public static string ReadAllText(string path) =>
        TryTable(path, out _) || TryFile(path, out _) ? Encoding.UTF8.GetString(ReadAllBytes(path)) : File.ReadAllText(path);

    public static IEnumerable<string> ReadLines(string path) =>
        TryTable(path, out _) || TryFile(path, out _) ? Lines(ReadAllText(path)) : File.ReadLines(path);

    public static Stream OpenRead(string path) =>
        TryTable(path, out _) || TryFile(path, out _) ? new MemoryStream(ReadAllBytes(path), writable: false) : File.OpenRead(path);

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

    /// <summary>Whether client.db has the table.</summary>
    public static bool Has(string table)
    {
        if (!File.Exists(DefaultPath)) return false;
        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $t";
        c.Parameters.AddWithValue("$t", table);
        return c.ExecuteScalar() is not null;
    }

    /// <summary>The table's rows as the JSON of its extracted file. Built once per version of client.db and kept,
    /// compressed, in its <c>client_json_cache</c> table (a rebuild of Mods takes a second; a cached read a fraction), and
    /// for a few seconds in memory, as several catalogues read the same tables in a burst.</summary>
    public static byte[] Json(string table, string? path = null)
    {
        lock (Recent)
            if (Recent.TryGetValue(table, out var recent))
            {
                Recent[table] = (recent.Bytes, DateTime.UtcNow);
                return recent.Bytes;
            }
        if (!Has(table))
            throw new FileNotFoundException($"the client table {table} is not in {DefaultPath} (the servers build it from sql/ at startup)",
                path ?? table);
        return Cache(table, db => Build(db, table));
    }

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
            Sweeper ??= new Timer(_ => Sweep(), null, Keep, Keep);
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

    static readonly TimeSpan Keep = TimeSpan.FromSeconds(3);
    static readonly Dictionary<string, (byte[] Bytes, DateTime Used)> Recent = new(StringComparer.Ordinal);
    static Timer? Sweeper;

    static void Sweep()
    {
        lock (Recent)
        {
            foreach (string table in Recent.Where(e => DateTime.UtcNow - e.Value.Used > Keep).Select(e => e.Key).ToList()) Recent.Remove(table);
            if (Recent.Count == 0) { Sweeper?.Dispose(); Sweeper = null; }
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

    static byte[] Build(SqliteConnection db, string table)
    {
        using var c = db.CreateCommand();
        c.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\" ORDER BY \"_row\"";
        using var r = c.ExecuteReader();
        int count = r.FieldCount;
        var names = new JsonEncodedText[count];
        var kinds = new Kind[count];
        for (int i = 1; i < count; i++)
        {
            names[i] = JsonEncodedText.Encode(r.GetName(i), JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
            kinds[i] = r.GetDataTypeName(i).ToUpperInvariant() switch
            {
                "INTEGER" => Kind.Integer,
                "REAL" => Kind.Real,
                "TEXT" => Kind.Text,
                "BOOLEAN" => Kind.Boolean,
                _ => Kind.Json,
            };
        }
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartArray();
            while (r.Read())
            {
                w.WriteStartObject();
                for (int i = 1; i < count; i++)
                {
                    w.WritePropertyName(names[i]);
                    if (r.IsDBNull(i)) { w.WriteNullValue(); continue; }
                    switch (kinds[i])
                    {
                        case Kind.Integer: w.WriteNumberValue(r.GetInt64(i)); break;
                        case Kind.Real: w.WriteNumberValue(r.GetDouble(i)); break;
                        case Kind.Text: w.WriteStringValue(r.GetString(i)); break;
                        case Kind.Boolean: w.WriteBooleanValue(r.GetInt64(i) != 0); break;
                        default: w.WriteRawValue(r.GetString(i), skipInputValidation: true); break;
                    }
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return buffer.ToArray();
    }

    enum Kind { Integer, Real, Text, Boolean, Json }

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
