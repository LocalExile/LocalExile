using System.Text.Json;


/// <summary>Native OT footprints for NPCs and functional scenery. Loot and waypoints stay nonblocking.</summary>
static class StationaryCollision
{
    internal sealed record Profile(int Size, bool Blocking, bool Static, string? Model)
    {
        public string? BlockingLeagueFlag { get; init; }
        public int BlockerSize(double? hull) => Blocking ? Math.Max(0, Size - 1) : hull > 0 ? 0 : -1;
    }

    static readonly Lazy<Dictionary<string, Profile>> Types = new(() =>
    {
        var types = Load("data/object-types.json");
        // NPCs.dat registers Id aliases on Metadata's OT (e.g. later-act versions of a town NPC).
        using var npcs = GameTables.Open("data/tables/NPCs.json");
        foreach (var npc in npcs.RootElement.EnumerateArray())
            if (npc.GetProperty("Id").GetString() is { } id && !types.ContainsKey(id)
                && npc.TryGetProperty("Metadata", out var m) && m.GetString() is { } metadata
                && types.TryGetValue(metadata, out var shape)) types[id] = shape;
        return types;
    });
    static readonly Lazy<Dictionary<string, double>> Radii = new(() =>
    {
        if (!File.Exists("data/hideouts.json")) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes("data/hideouts.json"));
        return doc.RootElement.TryGetProperty("collisionRadii", out var r)
            ? r.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal) : [];
    });

    public static Profile For(string type) => Types.Value.GetValueOrDefault(type)
        ?? throw new InvalidOperationException($"Missing collision metadata for {type}; run scripts/dev/extract-data.ps1");

    public static (int Size, double? Hull) Resolve(string type, string? model = null)
    {
        var p = For(type);
        double? hull = p.Static && Radii.Value.TryGetValue(model ?? p.Model ?? "", out double r) && r > 0 ? r : null;
        return (p.BlockerSize(hull), hull);
    }

    internal static Dictionary<string, Profile> Load(string path)
    {
        var result = new Dictionary<string, Profile>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
        {
            if (!t.TryGetProperty("positioned", out var p)) continue;
            string? Value(string key) => p.TryGetProperty(key, out var v) ? v.GetString() : null;
            int size = int.TryParse(Value("object_size"), out int n) ? n : 1;
            result[t.GetProperty("type").GetString()!] = new(Math.Max(1, size), Value("blocking") == "true",
                Value("static") == "true", t.TryGetProperty("animated_object", out var model) ? model.GetString() : null)
                { BlockingLeagueFlag = Value("block_league_flag") };
        }
        return result;
    }
}
