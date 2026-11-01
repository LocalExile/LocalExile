/// <summary>
/// The English stat descriptions (data/stat-descriptions.txt: Metadata/StatDescriptions/stat_descriptions.txt without its
/// translations, `game-pack stat-descriptions`): how the client words a mod's stats ("{0:+d} to maximum Life"). A
/// description covers one or more stats; each variant applies when every stat's value meets its condition ("#" any,
/// "5" exactly, "1|#" at least 1, "#|-1" at most -1, "!0" not 0), and its handlers ("negate 1") change a value before it
/// is shown. Tools read them through the Gateway's `crafter` mode; the server itself words nothing with them.
/// </summary>
sealed class StatDescriptions
{
    public const string DefaultFile = "data/stat-descriptions.txt";

    /// <param name="Conditions">One per stat, as written ("#", "1|#", "!0").</param>
    /// <param name="Handlers">(name, 1-based stat index), in order.</param>
    public sealed record Variant(IReadOnlyList<string> Conditions, string Text, IReadOnlyList<(string Name, int Stat)> Handlers);

    public sealed record Description(IReadOnlyList<string> Stats, IReadOnlyList<Variant> Variants);

    readonly Dictionary<string, Description> _byStat = new(StringComparer.Ordinal);

    public IReadOnlyList<Description> All { get; }

    StatDescriptions(List<Description> all)
    {
        All = all;
        foreach (var d in all)
            foreach (string stat in d.Stats) _byStat.TryAdd(stat, d);
    }

    /// <summary>The first description that covers the stat, or null.</summary>
    public Description? For(string stat) => _byStat.GetValueOrDefault(stat);

    public static StatDescriptions Load(string path = DefaultFile) =>
        File.Exists(path) ? Parse(File.ReadLines(path)) : new([]);

    public static StatDescriptions Parse(IEnumerable<string> lines)
    {
        var all = new List<Description>();
        using var e = lines.GetEnumerator();
        bool more = e.MoveNext();
        while (more)
        {
            if (e.Current.Trim() != "description" && !e.Current.StartsWith("description ", StringComparison.Ordinal)) { more = e.MoveNext(); continue; }
            // "\t2 stat_a stat_b", "\tN" variants, then N variant lines.
            if (!(more = e.MoveNext())) break;
            string[] head = e.Current.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (head.Length < 2 || !int.TryParse(head[0], out int statCount)) continue;
            var stats = head.Skip(1).Take(statCount).ToList();
            if (!(more = e.MoveNext())) break;
            if (!int.TryParse(e.Current.Trim(), out int count)) continue;
            var variants = new List<Variant>();
            for (int i = 0; i < count && (more = e.MoveNext()); i++)
                if (ParseVariant(e.Current, stats.Count) is { } v) variants.Add(v);
            all.Add(new(stats, variants));
            more = e.MoveNext();
        }
        return new(all);
    }

    /// <summary>`1|# "text" negate 1 reminderstring ReminderTextX canonical_line`: conditions, text, handlers.</summary>
    static Variant? ParseVariant(string line, int stats)
    {
        int open = line.IndexOf('"'), close = line.LastIndexOf('"');
        if (open < 0 || close <= open) return null;
        var conditions = line[..open].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(stats).ToList();
        while (conditions.Count < stats) conditions.Add("#");
        var handlers = new List<(string, int)>();
        string[] tail = line[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tail.Length; i++)
        {
            if (tail[i] == "reminderstring") { i++; continue; }
            if (i + 1 < tail.Length && int.TryParse(tail[i + 1], out int index)) { handlers.Add((tail[i], index)); i++; }
        }
        return new(conditions, Markup(line[(open + 1)..close]), handlers);
    }

    /// <summary>The text without the client's link markup: "[ContainsAbyss|Abyss]" is "Abyss", "[Toughness]" "Toughness".</summary>
    static string Markup(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]|]*)(?:\|([^\]]*))?\]", m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value);
}
