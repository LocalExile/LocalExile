using System.Text.Json;

/// <summary>Fixed curse flags on the target variety's own mods, separate from rolled rarity/affix effects.</summary>
sealed class MonsterCurseSources(ModCatalog mods, string folder = "data")
{
    static readonly HashSet<string> Flags = ["reflect_curses", "immune_to_curses", "hexproof", ActionSpeed.ActionFloor];
    readonly Lazy<Dictionary<int, Dictionary<string, int>>> sources = new(() => Load(mods, folder));

    public int Get(SpawnedMonster monster, string name) => monster.Mods.Effects[name]
        + (monster.Variety is { } variety && sources.Value.TryGetValue(variety.Row, out var stats)
            ? stats.GetValueOrDefault(name) : 0);

    static Dictionary<int, Dictionary<string, int>> Load(ModCatalog mods, string folder)
    {
        var result = new Dictionary<int, Dictionary<string, int>>();
        string path = Path.Combine(folder, "tables", "MonsterVarieties.json");
        if (!File.Exists(path)) return result;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        int row = 0;
        foreach (var variety in doc.RootElement.EnumerateArray())
        {
            var stats = new Dictionary<string, int>();
            if (variety.TryGetProperty("ModsKeys", out var keys))
                foreach (var key in keys.EnumerateArray())
                    if (mods.Row(key.GetInt32()) is { } mod)
                        foreach (var stat in mod.Stats.Where(s => Flags.Contains(s.Id) && s.Min == s.Max))
                            stats[stat.Id] = stats.GetValueOrDefault(stat.Id) + stat.Min;
            if (stats.Count > 0) result[row] = stats;
            row++;
        }
        return result;
    }
}
