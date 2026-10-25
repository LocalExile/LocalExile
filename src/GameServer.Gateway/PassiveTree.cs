using System.Text.Json;

/// <summary>
/// The passive skill tree's nodes: PassiveSkills.dat rows by PassiveSkillGraphId (the id the client allocates and S→C
/// 0x3d lists), with their stats (Stats.dat rows + 1 = the client's stat keys) and the skill points they grant. Game data:
/// data/tables/PassiveSkills.json, loaded on first use. OrdinaryPassiveRules validates ordinary connectivity against
/// the selected generated PSG topology; AscendancyRules validates the separate ascendancy allocation.
/// Masteries: PassiveSkillMasteryGroups.dat groups a mastery node's options and PassiveSkillMasteryEffects.dat holds the
/// effects' stats. The client's 0x3d list carries the selected effects as node | effect &lt;&lt; 16; the effect is the
/// effect row's HASH16 (the value in the client's passive tree data), not its data-table row index.
/// Gold respec (Bestel's Respecialisation): a node costs VillageBalancePerLevelShared.dat's GoldRespecCost at the
/// character's level times the node's multiplier, as the client's per-node cost 0x141efb140 computes it.
/// </summary>
sealed class PassiveTree(string folder = "data")
{
    public sealed record Node(ushort GraphId, string Id, string Name, IReadOnlyList<(uint Key, int Value)> Stats, int PointsGranted,
        bool IsAscendancy, bool IsStart, int MasteryGroup, int RespecMultiplier = 1);

    public sealed record MasteryEffect(int Row, ushort Hash, string Id, IReadOnlyList<(uint Key, int Value)> Stats);

    private readonly Lazy<Dictionary<ushort, Node>> _nodes = new(() => LoadNodes(folder));
    private readonly Lazy<Dictionary<int, MasteryEffect>> _effects = new(() => LoadEffects(folder));
    private readonly Lazy<Dictionary<int, List<MasteryEffect>>> _groups = new(() => LoadGroups(folder, LoadEffects(folder)));
    private readonly Lazy<Dictionary<int, int>> _respecCosts = new(() => LoadRespecCosts(folder));

    public Node? Find(ushort graphId) => _nodes.Value.GetValueOrDefault(graphId);

    public bool Loaded => _nodes.Value.Count > 0;

    /// <summary>The effects a mastery node offers, in the table's order (the client shows them in this order).</summary>
    public IReadOnlyList<MasteryEffect> EffectsFor(ushort graphId) =>
        Find(graphId) is { MasteryGroup: >= 0 } node && _groups.Value.TryGetValue(node.MasteryGroup, out var effects)
            ? effects : [];

    /// <summary>The effect by the client's HASH16, falling back to the data-table row index.</summary>
    public MasteryEffect? FindEffect(int id) =>
        _effects.Value.Values.FirstOrDefault(e => e.Hash == id) ?? _effects.Value.GetValueOrDefault(id);

    /// <summary>The skill points the allocated nodes grant themselves (the client counts them as extra points).</summary>
    public int PointsGranted(IEnumerable<ushort> nodes) => nodes.Sum(n => Find(n)?.PointsGranted ?? 0);

    /// <summary>
    /// The gold a respec of these nodes costs at the character's level (client 0x141efb140): GoldRespecCost of the
    /// VillageBalancePerLevelShared.dat row for the level, times each node's multiplier (1; 0 for a multiple-choice
    /// node; an expansion node's own value). Ascendancy nodes are not part of a gold respec (the client's full cost
    /// skips them too). Unknown nodes cost nothing.
    /// </summary>
    public long GoldRespecCost(IEnumerable<ushort> nodes, int level)
    {
        long perNode = _respecCosts.Value.GetValueOrDefault(level);
        return nodes.Sum(n => Find(n) is { IsAscendancy: false } node ? perNode * node.RespecMultiplier : 0);
    }

    /// <summary>The allocated nodes' stats, with their selected mastery effects, summed by key.</summary>
    public Dictionary<uint, int> Stats(IEnumerable<ushort> nodes, IReadOnlyDictionary<ushort, ushort>? masteries = null)
    {
        var sum = new Dictionary<uint, int>();
        foreach (var n in nodes)
        {
            if (Find(n) is { } node)
                foreach (var (k, v) in node.Stats) sum[k] = sum.GetValueOrDefault(k) + v;
            if (masteries is not null && masteries.TryGetValue(n, out var effect) && FindEffect(effect) is { } mastery)
                foreach (var (k, v) in mastery.Stats) sum[k] = sum.GetValueOrDefault(k) + v;
        }
        return sum;
    }

    /// <summary>Whether the effect belongs to the mastery node's group (the client only offers those).</summary>
    public bool IsEffectFor(ushort graphId, MasteryEffect effect) =>
        EffectsFor(graphId).Any(e => e.Row == effect.Row && e.Hash == effect.Hash);

    private static Dictionary<ushort, Node> LoadNodes(string folder)
    {
        string path = Path.Combine(folder, "tables", "PassiveSkills.json");
        var nodes = new Dictionary<ushort, Node>();
        if (!File.Exists(path)) return nodes;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var r in d.RootElement.EnumerateArray())
        {
            int graphId = r.GetProperty("PassiveSkillGraphId").GetInt32();
            if (graphId is <= 0 or > ushort.MaxValue) continue;
            var keys = r.GetProperty("Stats").EnumerateArray().Select(s => s.GetInt32()).ToList();
            var stats = new List<(uint, int)>();
            for (int i = 0; i < keys.Count && i < 5; i++)
                stats.Add(((uint)keys[i] + 1, r.GetProperty($"Stat{i + 1}Value").GetInt32()));
            int masteryGroup = r.TryGetProperty("MasteryGroup", out var group) && group.ValueKind == JsonValueKind.Number
                ? group.GetInt32() : -1;
            // The client's multiplier: an expansion node's own value (unnamed column at +0xb2), 0 for a multiple-choice node.
            int respec = r.GetProperty("IsExpansion").GetBoolean() ? r.GetProperty("__col26").GetInt32()
                : r.GetProperty("IsMultipleChoice").GetBoolean() ? 0 : 1;
            nodes.TryAdd((ushort)graphId, new Node((ushort)graphId, r.GetProperty("Id").GetString() ?? "", r.GetProperty("Name").GetString() ?? "",
                stats, r.GetProperty("SkillPointsGranted").GetInt32(), r.GetProperty("AscendancyKey").ValueKind == JsonValueKind.Number,
                r.GetProperty("Characters").GetArrayLength() > 0, masteryGroup, respec));
        }
        return nodes;
    }

    private static Dictionary<int, int> LoadRespecCosts(string folder)
    {
        string path = Path.Combine(folder, "tables", "VillageBalancePerLevelShared.json");
        var costs = new Dictionary<int, int>();
        if (!File.Exists(path)) return costs;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        // The client looks the row up by its first column (the level, 1–100).
        foreach (var r in d.RootElement.EnumerateArray())
            costs[r.GetProperty("MaxAccountLevel").GetInt32()] = r.GetProperty("GoldRespecCost").GetInt32();
        return costs;
    }

    private static Dictionary<int, MasteryEffect> LoadEffects(string folder)
    {
        string path = Path.Combine(folder, "tables", "PassiveSkillMasteryEffects.json");
        var effects = new Dictionary<int, MasteryEffect>();
        if (!File.Exists(path)) return effects;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        int row = 0;
        foreach (var r in d.RootElement.EnumerateArray())
        {
            var keys = r.GetProperty("Stats").EnumerateArray().Select(s => s.GetInt32()).ToList();
            var stats = new List<(uint, int)>();
            for (int i = 0; i < keys.Count && i < 3; i++)
                stats.Add(((uint)keys[i] + 1, r.GetProperty($"Stat{i + 1}Value").GetInt32()));
            int hash = r.GetProperty("HASH16").GetInt32();
            effects[row] = new MasteryEffect(row, (ushort)hash, r.GetProperty("Id").GetString() ?? "", stats);
            row++;
        }
        return effects;
    }

    private static Dictionary<int, List<MasteryEffect>> LoadGroups(string folder, Dictionary<int, MasteryEffect> effects)
    {
        string path = Path.Combine(folder, "tables", "PassiveSkillMasteryGroups.json");
        var groups = new Dictionary<int, List<MasteryEffect>>();
        if (!File.Exists(path)) return groups;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        int row = 0;
        foreach (var r in d.RootElement.EnumerateArray())
        {
            var list = new List<MasteryEffect>();
            foreach (var e in r.GetProperty("MasteryEffects").EnumerateArray())
                if (effects.TryGetValue(e.GetInt32(), out var effect)) list.Add(effect);
            groups[row++] = list;
        }
        return groups;
    }
}
