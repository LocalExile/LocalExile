using System.Text.Json;

/// <summary>
/// Ordinary allocations must remain connected to the character's implicit start in the selected client graph.
/// Callers validate the ascendancy allocation separately, then supply its candidate nodes so refunding an Ascendant
/// Path of the class cannot leave an ordinary branch depending on the removed additional start.
/// </summary>
sealed class OrdinaryPassiveRules(string folder = "data")
{
    sealed record Node(bool IsAscendancy, bool IsMastery, bool RequiresExpansion,
        IReadOnlyList<byte> Starts, byte? GrantedStart);
    sealed record Data(IReadOnlyDictionary<ushort, Node> Nodes, IReadOnlyDictionary<byte, ushort> Starts,
        IReadOnlyDictionary<AscendancyFamily, Dictionary<ushort, HashSet<ushort>>> Graphs);

    readonly Lazy<Data?> _data = new(() => Load(folder));

    public bool Loaded => _data.Value is not null;
    public bool GraphLoaded => Loaded;

    /// <summary>Checks a complete candidate set without modifying either supplied allocation.</summary>
    public void Validate(byte baseClass, AscendancyFamily family, IEnumerable<ushort> nodes,
        IEnumerable<ushort>? ascendancyNodes = null)
    {
        ushort[] requested = nodes.ToArray();
        // An empty/reset ordinary tree needs no graph and cannot contain an orphaned branch.
        if (requested.Length == 0) return;
        var data = _data.Value ?? throw new AscendancyRuleException(
            "Passive graph data is missing; run scripts/dev/extract-data.ps1");
        if (!data.Graphs.TryGetValue(family, out var graph) || !data.Starts.TryGetValue(baseClass, out ushort start) ||
            !graph.ContainsKey(start))
            throw new AscendancyRuleException($"No ordinary passive start for class {baseClass} in the {family} client graph");

        var allocated = new HashSet<ushort>();
        foreach (ushort id in requested)
        {
            if (!allocated.Add(id)) throw new AscendancyRuleException($"Duplicate ordinary passive {id}");
            if (!graph.ContainsKey(id) || !data.Nodes.TryGetValue(id, out var node))
                throw new AscendancyRuleException($"Ordinary passive {id} is absent from the {family} client graph");
            if (node.IsAscendancy) throw new AscendancyRuleException($"Ascendancy passive {id} is not an ordinary passive");
            if (node.Starts.Count != 0) throw new AscendancyRuleException($"Class start {id} is implicit and cannot be allocated");
            if (node.RequiresExpansion)
                throw new AscendancyRuleException($"Passive {id} requires unsupported expansion/cluster jewel topology");
        }

        var roots = new HashSet<ushort> { start };
        foreach (ushort id in ascendancyNodes ?? [])
        {
            if (!graph.ContainsKey(id) || !data.Nodes.TryGetValue(id, out var node) || !node.IsAscendancy)
                throw new AscendancyRuleException($"Ascendancy passive {id} is absent from the {family} client graph");
            if (node.GrantedStart is { } granted && data.Starts.TryGetValue(granted, out ushort additional) &&
                graph.ContainsKey(additional))
                roots.Add(additional);
        }

        var reached = new HashSet<ushort>(roots);
        var queue = new Queue<ushort>(roots);
        while (queue.TryDequeue(out ushort current))
        {
            // A mastery can be selected from any adjacent allocated notable; it cannot connect those notables.
            if (data.Nodes[current].IsMastery) continue;
            foreach (ushort next in graph[current])
                if (allocated.Contains(next) && reached.Add(next)) queue.Enqueue(next);
        }
        if (allocated.Any(id => !reached.Contains(id)))
            throw new AscendancyRuleException("Ordinary passive nodes must remain connected to an available class start");
    }

    static Data? Load(string folder)
    {
        string graphPath = Path.Combine(folder, AscendancyCatalog.GraphFile);
        string passivePath = Path.Combine(folder, "tables", "PassiveSkills.json");
        string characterPath = Path.Combine(folder, "tables", "Characters.json");
        string statsPath = Path.Combine(folder, "tables", "Stats.json");
        if (!new[] { graphPath, passivePath, characterPath, statsPath }.All(File.Exists)) return null;

        using var graphs = JsonDocument.Parse(File.ReadAllBytes(graphPath));
        if (graphs.RootElement.GetProperty("revision").GetString() != AscendancyCatalog.Revision)
            throw new InvalidDataException($"{graphPath}: expected passive data revision {AscendancyCatalog.Revision}");
        var links = Enum.GetValues<AscendancyFamily>().ToDictionary(f => f, f =>
            ReadGraph(graphs.RootElement.GetProperty(f == AscendancyFamily.Normal ? "normal" : "phrecia")));
        using var characters = JsonDocument.Parse(File.ReadAllBytes(characterPath));
        byte[] classes = characters.RootElement.EnumerateArray()
            .Select(r => checked((byte)r.GetProperty("IntegerId").GetInt32())).ToArray();
        using var stats = JsonDocument.Parse(File.ReadAllBytes(statsPath));
        int grantStat = Array.FindIndex(stats.RootElement.EnumerateArray().ToArray(),
            r => r.GetProperty("Id").GetString() == "display_can_take_character_start_point");
        using var passives = JsonDocument.Parse(File.ReadAllBytes(passivePath));
        var nodes = new Dictionary<ushort, Node>();
        var starts = new Dictionary<byte, ushort>();
        foreach (var row in passives.RootElement.EnumerateArray())
        {
            int id = row.GetProperty("PassiveSkillGraphId").GetInt32();
            if (id is <= 0 or > ushort.MaxValue) continue;
            byte[] startClasses = row.GetProperty("Characters").EnumerateArray().Select(c =>
            {
                int index = c.GetInt32();
                if (index < 0 || index >= classes.Length)
                    throw new InvalidDataException($"{passivePath}: passive {id} has unknown character row {index}");
                return classes[index];
            }).ToArray();
            byte? grantedStart = null;
            int statIndex = 0;
            foreach (var stat in row.GetProperty("Stats").EnumerateArray())
            {
                if (grantStat >= 0 && stat.GetInt32() == grantStat)
                {
                    int targetClass = row.GetProperty($"Stat{statIndex + 1}Value").GetInt32();
                    if (targetClass < 0 || targetClass > byte.MaxValue || !classes.Contains((byte)targetClass))
                        throw new InvalidDataException($"{passivePath}: passive {id} grants unknown class start {targetClass}");
                    grantedStart = (byte)targetClass;
                }
                statIndex++;
            }
            if (!nodes.TryAdd((ushort)id, new(row.GetProperty("AscendancyKey").ValueKind == JsonValueKind.Number,
                    row.TryGetProperty("MasteryGroup", out var mastery) && mastery.ValueKind == JsonValueKind.Number &&
                        mastery.GetInt32() >= 0,
                    row.TryGetProperty("IsExpansion", out var expansion) && expansion.ValueKind == JsonValueKind.True ||
                        row.TryGetProperty("IsProxyPassive", out var proxy) && proxy.ValueKind == JsonValueKind.True,
                    startClasses, grantedStart)))
                throw new InvalidDataException($"{passivePath}: duplicate passive graph id {id}");
            foreach (byte startClass in startClasses)
                if (!starts.TryAdd(startClass, (ushort)id))
                    throw new InvalidDataException($"{passivePath}: duplicate start for class {startClass}");
        }
        return new(nodes, starts, links);
    }

    static Dictionary<ushort, HashSet<ushort>> ReadGraph(JsonElement graph)
    {
        var result = new Dictionary<ushort, HashSet<ushort>>();
        foreach (var node in graph.GetProperty("nodes").EnumerateArray())
        {
            ushort id = checked((ushort)node.GetProperty("id").GetInt32());
            if (!result.TryAdd(id, node.GetProperty("adjacent").EnumerateArray()
                    .Select(n => checked((ushort)n.GetInt32())).ToHashSet()))
                throw new InvalidDataException($"Duplicate passive graph node {id}");
        }
        // PSG drawing edges can be stored in either direction; allocation connectivity is undirected.
        foreach (var (id, adjacent) in result.ToArray())
            foreach (ushort other in adjacent.ToArray())
            {
                if (!result.TryGetValue(other, out var reverse))
                    throw new InvalidDataException($"Passive graph node {id} links to missing node {other}");
                reverse.Add(id);
            }
        return result;
    }
}
