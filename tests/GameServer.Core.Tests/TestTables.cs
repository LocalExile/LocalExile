using System.Text.Json;

namespace GameServer.Core.Tests;

/// <summary>Client tables for tests: written inline as JSON rows, or read from the repository's data/client.db.</summary>
static class TestTables
{
    /// <summary>A table from a JSON array of row objects (numbers, strings, booleans, null, lists).</summary>
    public static ClientTable Rows(string name, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.EnumerateArray()
            .Select(r => (IReadOnlyDictionary<string, object?>)r.EnumerateObject().ToDictionary(p => p.Name, p => Value(p.Value)))
            .ToList();
        return ClientTable.FromRows(name, rows);
    }

    static object? Value(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt64(out long l) ? l : v.GetDouble(),
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => v.EnumerateArray().Select(Value).ToArray(),
        _ => null,
    };

    /// <summary>data/client.db above the test's folder, or null without the extracted game data.</summary>
    public static string? RealPath
    {
        get
        {
            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
                if (Path.Combine(dir.FullName, "data", "client.db") is var db && File.Exists(db)) return db;
            return null;
        }
    }

    /// <summary>The real table from data/client.db: a test that reads one is a ClientTablesFact.</summary>
    public static ClientTable Real(string name) => ClientTable.Load(RealPath!, name);
}
