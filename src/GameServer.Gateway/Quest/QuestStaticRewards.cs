using System.Text.Json;

/// <summary>
/// QuestStaticRewards.dat (Agents/docs/protocol/12-quests.md §5): what a quest flag gives for good. Column 1 is passive skill
/// points (a1q6/a1q7/a1q9-UsedRewardBook: 1, a10q3: 2), column 5 ascendancy points (LabyrinthComplete1–3: 2), column 7 refund
/// points (a1q8-UsedRewardBook, the Book of Regrets: 2), and StatsKeys/StatValues permanent stats (a2q1: 8% movement
/// speed, a2q2: 15% to all resistances, a2q3: 40 life; Kitava's −30% are mirrored by Game.ResistancePenalty).
/// <para>Passive and ascendancy points are derived from the flags every time (a cleared flag takes its point away again);
/// the client counts quest passive points in the 0x33 tree's <c>extra_skill_points</c> (its check knows only
/// points_from_level, skill_points_from_passives and extra_skill_points, 15-progression.md §2), so the server sends them
/// there. Refund points are spent, so a flag credits its refund points once (the save's QuestRefundsCredited).</para>
/// </summary>
sealed class QuestStaticRewards
{
    public sealed record Reward(string Flag, int PassivePoints, int AscendancyPoints, int RefundPoints, IReadOnlyList<(string Stat, int Value)> Stats);

    readonly Dictionary<string, Reward> byFlag = new(StringComparer.Ordinal);
    public IReadOnlyCollection<Reward> Rewards => byFlag.Values;
    public Reward? Find(string flag) => byFlag.GetValueOrDefault(flag);

    /// <summary>Passive skill points the flags give.</summary>
    public int PassivePoints(IEnumerable<string> flags) => flags.Distinct().Sum(f => Find(f)?.PassivePoints ?? 0);

    /// <summary>Ascendancy points the flags give.</summary>
    public int AscendancyPoints(IEnumerable<string> flags) => flags.Distinct().Sum(f => Find(f)?.AscendancyPoints ?? 0);

    /// <summary>Refund points the flags give that <paramref name="credited"/> has not had yet, and the flags to add to it.</summary>
    public (int Points, List<string> Flags) NewRefunds(IEnumerable<string> flags, IReadOnlyCollection<string> credited)
    {
        var due = flags.Distinct().Where(f => !credited.Contains(f) && Find(f)?.RefundPoints is > 0).ToList();
        return (due.Sum(f => Find(f)!.RefundPoints), due);
    }

    /// <summary>The permanent stats the flags give, summed per stat. Flags every character has (Quests.EveryCharacter, e.g.
    /// EndGameAvailable, whose row is Kitava's Act 10 penalty) are not quest progress and give nothing here.</summary>
    public Dictionary<string, int> Stats(IEnumerable<string> flags)
    {
        var sum = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in flags.Distinct().Where(f => !Quests.EveryCharacter.Contains(f)))
            foreach (var (stat, value) in Find(f)?.Stats ?? [])
                sum[stat] = sum.GetValueOrDefault(stat) + value;
        return sum;
    }

    public static readonly Lazy<QuestStaticRewards> Default = new(() => Load());

    public static QuestStaticRewards Load(string folder = "data")
    {
        var result = new QuestStaticRewards();
        string table = Path.Combine(folder, "tables", "QuestStaticRewards.json"), flagsFile = Path.Combine(folder, "tables", "QuestFlags.json"),
            statsFile = Path.Combine(folder, "tables", "Stats.json");
        if (!File.Exists(table) || !File.Exists(flagsFile)) return result;
        string[] flags, stats = [];
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(flagsFile)))
            flags = [.. doc.RootElement.EnumerateArray().Select(f => f.GetProperty("Id").GetString() ?? "")];
        if (File.Exists(statsFile))
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(statsFile)))
                stats = [.. doc.RootElement.EnumerateArray().Select(s => s.GetProperty("Id").GetString() ?? "")];
        using var rows = JsonDocument.Parse(File.ReadAllBytes(table));
        foreach (var r in rows.RootElement.EnumerateArray())
        {
            if (r.GetProperty("QuestFlag") is not { ValueKind: JsonValueKind.Number } fv || fv.GetInt32() is not (>= 0 and var f) || f >= flags.Length) continue;
            int Int(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            var keys = r.TryGetProperty("StatsKeys", out var sk) ? sk.EnumerateArray().Select(k => k.GetInt32()).ToArray() : [];
            var values = r.TryGetProperty("StatValues", out var sv) ? sv.EnumerateArray().Select(k => k.GetInt32()).ToArray() : [];
            var statList = keys.Zip(values).Where(p => p.First >= 0 && p.First < stats.Length).Select(p => (stats[p.First], p.Second)).ToList();
            result.byFlag[flags[f]] = new(flags[f], Int("__col1"), Int("__col5"), Int("__col7"), statList);
        }
        return result;
    }
}
