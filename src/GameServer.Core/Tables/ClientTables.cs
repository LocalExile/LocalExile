using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameServer.Core;

/// <summary>
/// The client's tables, read from <c>data/client.db</c> (Agents/docs/dev/databases.md): the database the servers build from
/// <c>sql/base/client/</c> and <c>sql/updates/client/</c> (+ <c>client_fixes/</c>). A table is still named by the path of
/// its extracted file, <c>data/tables/&lt;Table&gt;.json</c>, and comes back as that file's JSON (an array of row objects,
/// the columns in order), rebuilt from the table's rows and declared column types. Any other path is read from disk, so
/// a reader can take a folder of its own (a test's) and still go through here.
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

    /// <summary>File.Exists for a table path: whether client.db has the table.</summary>
    public static bool Exists(string path) => TryTable(path, out string table) ? Has(table) : File.Exists(path);

    public static byte[] ReadAllBytes(string path) => TryTable(path, out string table) ? Json(table, path) : File.ReadAllBytes(path);

    public static string ReadAllText(string path) =>
        TryTable(path, out string table) ? Encoding.UTF8.GetString(Json(table, path)) : File.ReadAllText(path);

    public static Stream OpenRead(string path) =>
        TryTable(path, out string table) ? new MemoryStream(Json(table, path), writable: false) : File.OpenRead(path);

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
        byte[] bytes;
        using (var db = Open())
        {
            string version = Version(db);
            bytes = Cached(db, table, version) ?? Store(db, table, version, Build(db, table));
        }
        lock (Recent)
        {
            Recent[table] = (bytes, DateTime.UtcNow);
            Sweeper ??= new Timer(_ => Sweep(), null, Keep, Keep);
        }
        return bytes;
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
