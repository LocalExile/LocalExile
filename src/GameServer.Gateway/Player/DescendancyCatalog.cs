using GameServer.Core;

/// <summary>
/// The Descendancy.dat rows: a second ascendancy layer the client draws beside the main one (0x33's spare byte selects
/// the row). Affliction's Wildwood specialisations (Warden of the Maji, Warlock of the Mists, Wildwood Primalist) and the
/// 3.29 Bloodline classes are its rows; each node's <c>DescendancyKey</c> in PassiveSkills.dat names its row.
/// </summary>
public sealed class DescendancyCatalog
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

    /// <summary>The quest flag that gates a row, or null when its <c>UnlockCondition</c> is empty. The three Wildwood
    /// rows are switched at their Wildwood NPC, which sets the Azmeri class flag; the Bloodline rows carry no condition
    /// in <c>Descendancy.dat</c>.</summary>
    public static string? UnlockFlag(byte row) => row switch
    {
        1 => "AzmeriAreWarden",
        2 => "AzmeriAreVoodooShaman",
        3 => "AzmeriArePrimalist",
        _ => null,
    };

    readonly Lazy<IReadOnlyList<Row>> _rows = new(Load);

    public IReadOnlyList<Row> Rows => _rows.Value;

    public Row? Find(string name) => Rows.FirstOrDefault(r => r.Index != 0 &&
        (r.Id.Equals(name, StringComparison.OrdinalIgnoreCase) || r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    readonly Lazy<IReadOnlyDictionary<byte, ushort>> _startNodes = new(LoadStartNodes);

    /// <summary>The row's implicit start node (PassiveSkills <c>IsAscendancyStartingNode</c>), if the data has one.</summary>
    public ushort? StartNode(byte row) => _startNodes.Value.TryGetValue(row, out ushort node) ? node : null;

    static IReadOnlyDictionary<byte, ushort> LoadStartNodes()
    {
        var starts = new Dictionary<byte, ushort>();
        if (!ClientTables.Has("PassiveSkills")) return starts;
        foreach (var r in ClientTables.Table("PassiveSkills"))
        {
            if (r.IntOrNull("DescendancyKey") is not int key || key is <= 0 or > byte.MaxValue) continue;
            if (r.BoolOrNull("IsAscendancyStartingNode") != true) continue;
            int id = r.Int("PassiveSkillGraphId");
            if (id is <= 0 or > ushort.MaxValue) continue;
            starts[checked((byte)key)] = checked((ushort)id);
        }
        return starts;
    }

    readonly Lazy<IReadOnlyDictionary<byte, Definition>> _definitions = new(LoadDefinitions);

    /// <summary>The row's full tree: nodes, their links in the Default graph and the implicit start.</summary>
    public Definition? Tree(byte row) => _definitions.Value.GetValueOrDefault(row);

    static IReadOnlyDictionary<byte, Definition> LoadDefinitions()
    {
        var definitions = new Dictionary<byte, Definition>();
        if (!ClientTables.Has("PassiveSkills")) return definitions;
        var links = PassiveGraphs.Available ? PassiveGraphs.Links(AscendancyFamily.Normal) : [];
        var perRow = new Dictionary<byte, Dictionary<ushort, Node>>();
        foreach (var r in ClientTables.Table("PassiveSkills"))
        {
            if (r.IntOrNull("DescendancyKey") is not int key || key is <= 0 or > byte.MaxValue) continue;
            int id = r.Int("PassiveSkillGraphId");
            if (id is <= 0 or > ushort.MaxValue) continue;
            byte row = checked((byte)key);
            ushort nodeId = checked((ushort)id);
            var node = new Node(nodeId, links.TryGetValue(nodeId, out var adjacent) ? [.. adjacent] : [],
                r.BoolOrNull("IsAscendancyStartingNode") == true,
                r.BoolOrNull("IsMultipleChoice") == true,
                r.BoolOrNull("IsMultipleChoiceOption") == true);
            if (!perRow.TryGetValue(row, out var nodes)) perRow[row] = nodes = [];
            nodes[nodeId] = node;
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

    static IReadOnlyList<Row> Load()
    {
        var rows = new List<Row>();
        if (!ClientTables.Has("Descendancy")) return rows;
        int index = 0;
        foreach (var r in ClientTables.Table("Descendancy"))
        {
            if (index > byte.MaxValue) throw new InvalidDataException("Descendancy: more than 255 rows");
            rows.Add(new(checked((byte)index++), r.Text("Id") ?? "", r.Text("Name") ?? ""));
        }
        return rows;
    }
}
