using GameServer.Core;

/// <summary>
/// The gem families the passive tree's family gem-level stats raise, from the current client's own metadata (never
/// display names): a GemTags row whose stat columns carry the stat (the Herald, Vaal and Link tags) puts the gems
/// carrying that tag in the stat's family, and a SkillGems MinionGlobalSkillLevelStat of the stat (Raise Zombie's)
/// puts every variant of that gem in. Other membership comes from the selected variant's GemEffects row, which
/// also supplies both skills a Vaal gem can grant. Base-item lookups include all variants for callers without an
/// equipped gem instance. Supports carry family tags too, so callers decide which bonuses may raise supports.
/// Reads the client tables (client.db, in memory); without them no stat has members.
/// </summary>
public sealed class SkillGemFamilies
{
    public SkillGemFamilies(string folder = "data") => (_members, _variants) = Load();

    sealed record VariantFamilies(HashSet<int> Stats, HashSet<string> Tags);

    readonly Dictionary<int, HashSet<int>> _members;
    readonly Dictionary<(int Gem, int Variant), VariantFamilies> _variants;

    // A family keyed by no stat row: the gems carrying the GemTags "exceptional" tag (Empower, Enlighten, ...).
    const int ExceptionalFamily = -1;

    /// <summary>Whether the Stats.dat row raises this gem's level: the gem's SkillGems row (the base item type's row)
    /// is in the stat's family. Unknown stats and rows are in no family.</summary>
    public bool Raises(int statRow, int gemRow) =>
        gemRow >= 0 && _members.TryGetValue(statRow, out var rows) && rows.Contains(gemRow);

    /// <summary>Whether the gem (its base item type's row) carries the Exceptional tag.</summary>
    public bool IsExceptional(int gemRow) => Raises(ExceptionalFamily, gemRow);

    public bool Raises(int statRow, int gemRow, int variantIndex) =>
        _variants.TryGetValue((gemRow, variantIndex), out var variant) && variant.Stats.Contains(statRow);

    public bool IsExceptional(int gemRow, int variantIndex) => Raises(ExceptionalFamily, gemRow, variantIndex);

    public bool HasTag(int gemRow, int variantIndex, string tag) =>
        _variants.TryGetValue((gemRow, variantIndex), out var variant) && variant.Tags.Contains(tag);

    static (Dictionary<int, HashSet<int>> Members, Dictionary<(int Gem, int Variant), VariantFamilies> Variants) Load()
    {
        string[] needed = ["GemTags", "GemEffects", "SkillGems"];
        var members = new Dictionary<int, HashSet<int>>();
        var variants = new Dictionary<(int Gem, int Variant), VariantFamilies>();
        if (needed.Any(n => !ClientTables.Has(n))) return (members, variants);
        var tags = ClientTables.Table("GemTags");
        var effects = ClientTables.Table("GemEffects");
        var gems = ClientTables.Table("SkillGems");
        static int Int(ClientRow r, string name) => (r.IntOrNull(name) ?? -1);
        // A GemTags row's stat rows (Stat1..Stat4): the family stats whose gems carry the tag.
        var tagStats = new Dictionary<int, int[]>();
        var tagNames = new Dictionary<int, string>();
        int row = 0;
        foreach (var t in tags)
        {
            int[] stats = [.. new[] { Int(t, "Stat1"), Int(t, "Stat2"), Int(t, "Stat3"), Int(t, "Stat4") }.Where(s => s >= 0).Distinct()];
            if (t.TextOrNull("Id") is { } name)
            {
                tagNames[row] = name;
                if (name == "exceptional") stats = [ExceptionalFamily];
            }
            if (stats.Length > 0) tagStats[row] = stats;
            row++;
        }
        foreach (var gem in gems)
        {
            int gemRow = Int(gem, "BaseItemTypesKey");
            if (gemRow < 0) continue;
            int global = Int(gem, "MinionGlobalSkillLevelStat");
            if (global >= 0) Add(global, gemRow);
            int index = -1;
            foreach (int effect in gem.Ints("GemVariants"))
            {
                ++index;
                if (effect < 0 || effect >= effects.Count) continue;
                var variant = new VariantFamilies([], new(StringComparer.Ordinal));
                if (global >= 0) variant.Stats.Add(global);
                foreach (int tagRow in effects[effect].Ints("GemTags"))
                {
                    if (tagNames.TryGetValue(tagRow, out string? tag)) variant.Tags.Add(tag);
                    if (tagStats.TryGetValue(tagRow, out var stats)) variant.Stats.UnionWith(stats);
                }
                variants.TryAdd((gemRow, index), variant);
                foreach (int stat in variant.Stats) Add(stat, gemRow);
            }
        }
        return (members, variants);

        void Add(int statRow, int gemRow)
        {
            if (!members.TryGetValue(statRow, out var rows)) members[statRow] = rows = [];
            rows.Add(gemRow);
        }
    }
}
