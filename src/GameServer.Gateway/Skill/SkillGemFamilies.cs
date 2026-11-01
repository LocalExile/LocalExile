using System.Text.Json;

/// <summary>
/// The gem families the passive tree's family gem-level stats raise, from the current client's own metadata (never
/// display names): a GemTags row whose stat columns carry the stat (the Herald, Vaal and Link tags) puts the gems
/// carrying that tag in the stat's family, and a SkillGems MinionGlobalSkillLevelStat of the stat (Raise Zombie's)
/// puts that gem in. Membership is the gem's SkillGems row (ItemCatalog's GemType key, the base item type's row)
/// through any of its variants' tags — the same GemEffects rows the gem's skills come from — so a Vaal gem's bonus
/// raises both skills its one gem grants (its Vaal skill and its base skill) and a transfigured Raise Zombie keeps
/// its gem's family. Supports carry family tags too (Scornful Herald, Frigid Bond, the Vaal supports); the stats
/// raise skill gems only, so EffectiveGemLevel excludes supports when the bonus is applied.
/// Reads data/tables (game data, loaded on first use); without them no stat has members.
/// </summary>
sealed class SkillGemFamilies(string folder = "data")
{
    readonly Lazy<Dictionary<int, HashSet<int>>> _members = new(() => Load(folder));

    // A family keyed by no stat row: the gems carrying the GemTags "exceptional" tag (Empower, Enlighten, ...).
    const int ExceptionalFamily = -1;

    /// <summary>Whether the Stats.dat row raises this gem's level: the gem's SkillGems row (the base item type's row)
    /// is in the stat's family. Unknown stats and rows are in no family.</summary>
    public bool Raises(int statRow, int gemRow) =>
        gemRow >= 0 && _members.Value.TryGetValue(statRow, out var rows) && rows.Contains(gemRow);

    /// <summary>Whether the gem (its base item type's row) carries the Exceptional tag.</summary>
    public bool IsExceptional(int gemRow) => Raises(ExceptionalFamily, gemRow);

    // A Stats.dat row -> the base item type rows of the gems it raises: those whose MinionGlobalSkillLevelStat is the
    // stat, and those with a variant whose GemTags row carries the stat in its Stat1..Stat4 columns.
    static Dictionary<int, HashSet<int>> Load(string folder)
    {
        string Table(string n) => Path.Combine(folder, "tables", n + ".json");
        string[] needed = ["GemTags", "GemEffects", "SkillGems"];
        var members = new Dictionary<int, HashSet<int>>();
        if (needed.Any(n => !File.Exists(Table(n)))) return members;
        using var tags = JsonDocument.Parse(File.ReadAllBytes(Table("GemTags")));
        using var effects = JsonDocument.Parse(File.ReadAllBytes(Table("GemEffects")));
        using var gems = JsonDocument.Parse(File.ReadAllBytes(Table("SkillGems")));
        static int Int(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
        // A GemTags row's stat rows (Stat1..Stat4): the family stats whose gems carry the tag.
        var tagStats = new Dictionary<int, int[]>();
        int row = 0;
        foreach (var t in tags.RootElement.EnumerateArray())
        {
            int[] stats = [.. new[] { Int(t, "Stat1"), Int(t, "Stat2"), Int(t, "Stat3"), Int(t, "Stat4") }.Where(s => s >= 0).Distinct()];
            if (t.TryGetProperty("Id", out var id) && id.GetString() == "exceptional") stats = [ExceptionalFamily];
            if (stats.Length > 0) tagStats[row] = stats;
            row++;
        }
        int effectRows = effects.RootElement.GetArrayLength();
        row = 0;
        foreach (var g in gems.RootElement.EnumerateArray())
        {
            int gemRow = Int(g, "BaseItemTypesKey");
            if (gemRow >= 0)
            {
                if (Int(g, "MinionGlobalSkillLevelStat") is int global and >= 0) Add(global, gemRow);
                if (g.TryGetProperty("GemVariants", out var variants))
                    foreach (var v in variants.EnumerateArray())
                        if (v.ValueKind == JsonValueKind.Number && v.GetInt32() is int variant and >= 0 && variant < effectRows &&
                            effects.RootElement[variant].TryGetProperty("GemTags", out var carried))
                            foreach (var t in carried.EnumerateArray())
                                if (t.ValueKind == JsonValueKind.Number && tagStats.TryGetValue(t.GetInt32(), out var stats))
                                    foreach (int stat in stats) Add(stat, gemRow);
            }
            row++;
        }
        return members;

        void Add(int statRow, int gemRow)
        {
            if (!members.TryGetValue(statRow, out var rows)) members[statRow] = rows = [];
            rows.Add(gemRow);
        }
    }
}
