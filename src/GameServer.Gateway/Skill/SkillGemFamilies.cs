using System.Text.Json;
using GameServer.Core;

/// <summary>
/// The gem families the passive tree's family gem-level stats raise, from the current client's own metadata (never
/// display names): a GemTags row whose stat columns carry the stat (the Herald, Vaal and Link tags) puts the gems
/// carrying that tag in the stat's family, and a SkillGems MinionGlobalSkillLevelStat of the stat (Raise Zombie's)
/// puts that gem in. Membership is the gem's SkillGems row (ItemCatalog's GemType key, the base item type's row)
/// through any of its variants' tags — the same GemEffects rows the gem's skills come from — so a Vaal gem's bonus
/// raises both skills its one gem grants (its Vaal skill and its base skill) and a transfigured Raise Zombie keeps
/// its gem's family. Supports carry family tags too (Scornful Herald, Frigid Bond, the Vaal supports); the stats
/// raise skill gems only, so EffectiveGemLevel excludes supports when the bonus is applied.
/// Reads the client tables (client.db, loaded on first use); without them no stat has members.
/// </summary>
public sealed class SkillGemFamilies(string folder = "data")
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
        string[] needed = ["GemTags", "GemEffects", "SkillGems"];
        var members = new Dictionary<int, HashSet<int>>();
        if (needed.Any(n => !ClientTables.Has(n))) return members;
        var tags = ClientTables.Table("GemTags");
        var effects = ClientTables.Table("GemEffects");
        var gems = ClientTables.Table("SkillGems");
        static int Int(ClientRow r, string name) => (r.IntOrNull(name) ?? -1);
        // A GemTags row's stat rows (Stat1..Stat4): the family stats whose gems carry the tag.
        var tagStats = new Dictionary<int, int[]>();
        int row = 0;
        foreach (var t in tags)
        {
            int[] stats = [.. new[] { Int(t, "Stat1"), Int(t, "Stat2"), Int(t, "Stat3"), Int(t, "Stat4") }.Where(s => s >= 0).Distinct()];
            if (t.TextOrNull("Id") == "exceptional") stats = [ExceptionalFamily];
            if (stats.Length > 0) tagStats[row] = stats;
            row++;
        }
        int effectRows = effects.Count;
        row = 0;
        foreach (var g in gems)
        {
            int gemRow = Int(g, "BaseItemTypesKey");
            if (gemRow >= 0)
            {
                if (Int(g, "MinionGlobalSkillLevelStat") is int global and >= 0) Add(global, gemRow);
                foreach (int variant in g.Ints("GemVariants"))
                    if (variant >= 0 && variant < effectRows)
                        foreach (int t in effects[variant].Ints("GemTags"))
                            if (tagStats.TryGetValue(t, out var stats))
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
