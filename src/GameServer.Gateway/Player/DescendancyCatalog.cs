using System.Text.Json;

/// <summary>
/// The Descendancy.dat rows: a second ascendancy layer the client draws beside the main one (0x33's spare byte selects
/// the row). Affliction's Wildwood specialisations (Warden of the Maji, Warlock of the Mists, Wildwood Primalist) and the
/// 3.29 Bloodline classes are its rows; each node's <c>DescendancyKey</c> in PassiveSkills.dat names its row.
/// </summary>
sealed class DescendancyCatalog(string folder = "data")
{
    /// <summary>One row. <paramref name="Index"/> is the table row, the byte 0x33 carries; 0 is none.</summary>
    public sealed record Row(byte Index, string Id, string Name);

    /// <summary>One node of a row's tree; the adjacency comes from the client's Default passive graph.</summary>
    public sealed record Node(ushort Id, IReadOnlyList<ushort> Adjacent, bool IsStart, bool IsChoice, bool IsChoiceOption)
    {
        // A multiple-choice option is the effect of the paid parent, not a second ascendancy point.
        public int PointCost => IsStart || IsChoiceOption ? 0 : 1;
    }

    public sealed record Definition(byte Row, ushort StartNode, IReadOnlyDictionary<ushort, Node> Nodes);

    /// <summary>The three Affliction (Wildwood) rows keep their own eight points, as they had in that league; the 3.29
    /// Bloodline rows share the main ascendancy's points.</summary>
    public const int WildwoodPoints = 8;

    public static int BonusPoints(byte row) => row is >= 1 and <= 3 ? WildwoodPoints : 0;

    readonly Lazy<IReadOnlyList<Row>> _rows = new(() => Load(folder));

    public IReadOnlyList<Row> Rows => _rows.Value;

    public Row? Find(string name) => Rows.FirstOrDefault(r => r.Index != 0 &&
        (r.Id.Equals(name, StringComparison.OrdinalIgnoreCase) || r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    readonly Lazy<IReadOnlyDictionary<byte, ushort>> _startNodes = new(() => LoadStartNodes(folder));

    /// <summary>The row's implicit start node (PassiveSkills <c>IsAscendancyStartingNode</c>), if the data has one.</summary>
    public ushort? StartNode(byte row) => _startNodes.Value.TryGetValue(row, out ushort node) ? node : null;

    static IReadOnlyDictionary<byte, ushort> LoadStartNodes(string folder)
    {
        string path = Path.Combine(folder, "tables", "PassiveSkills.json");
        using var json = GameTables.Open(path);
        var starts = new Dictionary<byte, ushort>();
        foreach (var r in json.RootElement.EnumerateArray())
        {
            if (!r.TryGetProperty("DescendancyKey", out var key) || key.ValueKind != JsonValueKind.Number) continue;
            if (!r.GetProperty("IsAscendancyStartingNode").GetBoolean()) continue;
            int id = r.GetProperty("PassiveSkillGraphId").GetInt32();
            if (id is <= 0 or > ushort.MaxValue) continue;
            starts[checked((byte)key.GetInt32())] = checked((ushort)id);
        }
        return starts;
    }

    readonly Lazy<IReadOnlyDictionary<byte, Definition>> _definitions = new(() => LoadDefinitions(folder));

    /// <summary>The row's full tree: nodes, their links in the Default graph and the implicit start.</summary>
    public Definition? Tree(byte row) => _definitions.Value.GetValueOrDefault(row);

    static IReadOnlyDictionary<byte, Definition> LoadDefinitions(string folder)
    {
        string passivePath = Path.Combine(folder, "tables", "PassiveSkills.json");
        string graphPath = Path.Combine(folder, AscendancyCatalog.GraphFile);
        var definitions = new Dictionary<byte, Definition>();
        if (!File.Exists(passivePath) || !File.Exists(graphPath)) return definitions;
        var links = new Dictionary<ushort, ushort[]>();
        using (var graph = GameTables.Open(graphPath))
            foreach (var n in graph.RootElement.GetProperty("normal").GetProperty("nodes").EnumerateArray())
                links[checked((ushort)n.GetProperty("id").GetInt32())] =
                    n.GetProperty("adjacent").EnumerateArray().Select(a => checked((ushort)a.GetInt32())).ToArray();
        var perRow = new Dictionary<byte, Dictionary<ushort, Node>>();
        using var passives = GameTables.Open(passivePath);
        foreach (var r in passives.RootElement.EnumerateArray())
        {
            if (!r.TryGetProperty("DescendancyKey", out var key) || key.ValueKind != JsonValueKind.Number) continue;
            int id = r.GetProperty("PassiveSkillGraphId").GetInt32();
            if (id is <= 0 or > ushort.MaxValue) continue;
            byte row = checked((byte)key.GetInt32());
            if (row == 0) continue;
            var node = new Node((ushort)id, links.GetValueOrDefault((ushort)id, []),
                r.GetProperty("IsAscendancyStartingNode").GetBoolean(),
                r.GetProperty("IsMultipleChoice").GetBoolean(),
                r.GetProperty("IsMultipleChoiceOption").GetBoolean());
            if (!perRow.TryGetValue(row, out var nodes)) perRow[row] = nodes = [];
            nodes[(ushort)id] = node;
        }
        foreach (var (row, nodes) in perRow)
            definitions[row] = new Definition(row, nodes.Values.FirstOrDefault(n => n.IsStart)?.Id ?? 0, nodes);
        return definitions;
    }

    /// <summary>
    /// Checks a complete proposed layer allocation: every node belongs to <paramref name="row"/>, the set stays
    /// connected to the row's implicit start, and its points fit the layer's own budget (the Wildwood rows) and the
    /// shared ascendancy total together with the main ascendancy's <paramref name="spentElsewhere"/>.
    /// </summary>
    public void Validate(byte row, IEnumerable<ushort> allocated, int spentElsewhere, int mainPoints, int layerPoints)
    {
        if (row == 0) throw new AscendancyRuleException("No Descendancy is selected");
        var definition = Tree(row) ?? throw new AscendancyRuleException($"Descendancy row {row} has no tree data");
        if (definition.StartNode == 0) throw new AscendancyRuleException($"Descendancy row {row} has no start node");
        var set = new HashSet<ushort>();
        foreach (ushort n in allocated)
        {
            if (!set.Add(n)) throw new AscendancyRuleException($"Duplicate descendancy node {n}");
            if (!definition.Nodes.TryGetValue(n, out var node) || node.IsStart)
                throw new AscendancyRuleException($"Node {n} does not belong to this Descendancy's tree");
        }
        var reachable = new HashSet<ushort> { definition.StartNode };
        var queue = new Queue<ushort>();
        queue.Enqueue(definition.StartNode);
        while (queue.TryDequeue(out ushort current))
            foreach (ushort next in definition.Nodes[current].Adjacent)
                if (set.Contains(next) && reachable.Add(next)) queue.Enqueue(next);
        if (set.Any(n => !reachable.Contains(n)))
            throw new AscendancyRuleException("Descendancy nodes must remain connected to their start");
        var groups = new HashSet<ushort>();
        foreach (ushort option in set.Where(n => definition.Nodes[n].IsChoiceOption))
        {
            var parents = definition.Nodes[option].Adjacent
                .Where(n => definition.Nodes.TryGetValue(n, out var p) && p.IsChoice).ToArray();
            if (parents.Length != 1 || !set.Contains(parents[0]))
                throw new AscendancyRuleException($"Choice option {option} requires its allocated choice node");
            if (!groups.Add(parents[0]))
                throw new AscendancyRuleException("Only one option of a descendancy choice may be allocated");
        }
        int spent = set.Sum(n => definition.Nodes[n].PointCost);
        if (layerPoints > 0 && spent > layerPoints)
            throw new AscendancyRuleException($"Descendancy points exceeded: {spent} spent, {layerPoints} for this row");
        if (spentElsewhere + spent > mainPoints + layerPoints)
            throw new AscendancyRuleException(
                $"Ascendancy points exceeded: {spentElsewhere + spent} spent, {mainPoints + layerPoints} available");
    }

    static IReadOnlyList<Row> Load(string folder)
    {
        string path = Path.Combine(folder, "tables", "Descendancy.json");
        using var json = GameTables.Open(path);
        var rows = new List<Row>();
        int index = 0;
        foreach (var r in json.RootElement.EnumerateArray())
        {
            if (index > byte.MaxValue) throw new InvalidDataException($"{path}: more than 255 rows");
            rows.Add(new(checked((byte)index++), r.GetProperty("Id").GetString() ?? "",
                r.GetProperty("Name").GetString() ?? ""));
        }
        return rows;
    }
}
