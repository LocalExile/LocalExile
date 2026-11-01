using System.Text.Json;


/// <summary>
/// The mods an area has of its own (WorldAreas ModsKeys → Mods → Stats): The Twilight Strand has MapNoPortals
/// (map_disable_portal_use 1), so Portal Scrolls do nothing there. Built from data/tables; empty without them.
/// </summary>
sealed class AreaModCatalog(Dictionary<string, Dictionary<string, int>> stats)
{
    public const string DisablePortalUse = "map_disable_portal_use";

    /// <summary>The value of an area's own stat (summed over its mods), 0 when it has none.</summary>
    public int Stat(string areaId, string stat) => stats.GetValueOrDefault(areaId)?.GetValueOrDefault(stat) ?? 0;

    public bool DisablesPortals(string areaId) => Stat(areaId, DisablePortalUse) > 0;

    public static AreaModCatalog Load(string folder = "data")
    {
        string areas = Path.Combine(folder, "tables", "WorldAreas.json"), mods = Path.Combine(folder, "tables", "Mods.json"),
            statsFile = Path.Combine(folder, "tables", "Stats.json");
        if (!File.Exists(areas) || !File.Exists(mods) || !File.Exists(statsFile)) return new([]);
        try
        {
            // On a thread of its own, so the parse buffers (Mods.json) do not stay pooled (GameTables.Isolated).
            return GameTables.Isolated(() => Parse(File.ReadAllText(areas), File.ReadAllText(mods), File.ReadAllText(statsFile)));
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] area mods: {ex.Message}");
            return new([]);
        }
    }

    /// <summary>From the extracted WorldAreas, Mods and Stats tables (JSON arrays, row = index).</summary>
    public static AreaModCatalog Parse(string worldAreas, string modsJson, string statsJson)
    {
        using var s = JsonDocument.Parse(statsJson);
        var statIds = s.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString() ?? "").ToList();
        using var m = JsonDocument.Parse(modsJson);
        var modRows = m.RootElement.EnumerateArray().ToList();
        using var w = JsonDocument.Parse(worldAreas);
        var result = new Dictionary<string, Dictionary<string, int>>();
        foreach (var area in w.RootElement.EnumerateArray())
        {
            if (!area.TryGetProperty("ModsKeys", out var keys) || keys.ValueKind != JsonValueKind.Array) continue;
            var own = new Dictionary<string, int>();
            foreach (var k in keys.EnumerateArray())
            {
                if (k.ValueKind != JsonValueKind.Number || k.GetInt32() is not (>= 0 and var row) || row >= modRows.Count) continue;
                var mod = modRows[row];
                for (int i = 1; i <= 6; i++)
                {
                    if (!mod.TryGetProperty($"StatsKey{i}", out var key) || key.ValueKind != JsonValueKind.Number || key.GetInt32() >= statIds.Count) continue;
                    if (!mod.TryGetProperty($"Stat{i}Min", out var min) || min.ValueKind != JsonValueKind.Number) continue;
                    string stat = statIds[key.GetInt32()];
                    own[stat] = own.GetValueOrDefault(stat) + min.GetInt32();
                }
            }
            if (own.Count > 0) result[area.GetProperty("Id").GetString() ?? ""] = own;
        }
        return new(result);
    }
}
