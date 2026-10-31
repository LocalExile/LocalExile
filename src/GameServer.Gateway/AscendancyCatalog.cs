using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>The ordinary and Legacy of Phrecia trees are separate choices, even when they share a table row/start.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AscendancyFamily>))]
enum AscendancyFamily { Normal, Phrecia }

/// <summary>A durable choice. Table rows and the client's packed class byte are deliberately not save identifiers.</summary>
sealed record AscendancyIdentity(AscendancyFamily Family, string Id, string Revision)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>
/// Main ascendancies in the exact extracted client. Membership and links come from its two PSG files; the table alone
/// contains both families and is insufficient to authorise an allocation. Scavenger is one choice, not three Scion aliases.
/// </summary>
sealed class AscendancyCatalog(string folder = "data")
{
    public const string Revision = "3.29.3b";
    public const string GraphFile = "passive-skill-graphs.json";

    public sealed record Node(ushort Id, string PassiveId, string Name, IReadOnlyList<ushort> Adjacent,
        bool IsStart, int PointsGranted, bool IsChoice, bool IsChoiceOption,
        IReadOnlyList<(uint Key, int Value)> Stats)
    {
        // A multiple-choice option is the effect of the paid parent, not a second ascendancy point.
        public int PointCost => IsStart || IsChoiceOption ? 0 : 1;

        // Refund currency is a different calculation (client 0x141efb0e0): the choice shell is free to remove;
        // changing its selected effect costs five regrets, although that effect consumes no extra ascendancy point.
        public int RefundCost => IsStart || IsChoice ? 0 : AscendancyRules.RefundPointsPerAscendancyPoint;
    }

    public sealed record Definition(AscendancyIdentity Identity, string Name, byte BaseClass, int TableRow,
        byte ClassNo, ushort StartNode, IReadOnlyDictionary<ushort, Node> Nodes);

    readonly Lazy<IReadOnlyList<Definition>> _definitions = new(() => Load(folder));
    readonly Lazy<IReadOnlyDictionary<AscendancyFamily, HashSet<ushort>>> _graphMembership = new(() => LoadGraphMembership(folder));

    public IReadOnlyList<Definition> Definitions => _definitions.Value;
    public bool Loaded => Definitions.Count > 0;
    public bool GraphLoaded => _graphMembership.Value.Count == 2;
    public IEnumerable<Definition> Choices(byte baseClass) => Definitions.Where(d => d.BaseClass == baseClass);

    /// <summary>Never keep/apply a node that the chosen client graph cannot display. This check also protects old saves
    /// when a graph revision changes; the caller refuses the change/load and retains the original save.</summary>
    public void ValidateOrdinaryNodes(AscendancyFamily family, IEnumerable<ushort> nodes)
    {
        if (!_graphMembership.Value.TryGetValue(family, out var members))
            throw new AscendancyRuleException("Passive graph data is missing; run scripts/dev/extract-data.ps1");
        foreach (ushort id in nodes)
            if (!members.Contains(id))
                throw new AscendancyRuleException($"Ordinary passive {id} is absent from the {family} client graph; refund it before changing families");
    }

    static IReadOnlyDictionary<AscendancyFamily, HashSet<ushort>> LoadGraphMembership(string folder)
    {
        string path = Path.Combine(folder, GraphFile);
        if (!File.Exists(path)) return new Dictionary<AscendancyFamily, HashSet<ushort>>();
        using var graph = GameTables.Open(path);
        if (graph.RootElement.GetProperty("revision").GetString() != Revision)
            throw new InvalidDataException($"{path}: expected ascendancy data revision {Revision}");
        return Enum.GetValues<AscendancyFamily>().ToDictionary(f => f, f =>
            graph.RootElement.GetProperty(f == AscendancyFamily.Normal ? "normal" : "phrecia").GetProperty("nodes")
                .EnumerateArray().Select(n => checked((ushort)n.GetProperty("id").GetInt32())).ToHashSet());
    }

    public Definition? Find(AscendancyIdentity identity) => Definitions.FirstOrDefault(d =>
        d.Identity.Family == identity.Family && d.Identity.Id == identity.Id && d.Identity.Revision == identity.Revision);

    /// <summary>Names (including spaces) and stable table Ids accepted by the local selection command.</summary>
    public Definition? Find(byte baseClass, AscendancyFamily family, string name) => Choices(baseClass).FirstOrDefault(d =>
        d.Identity.Family == family && (string.Equals(d.Identity.Id, name, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)));

    static IReadOnlyList<Definition> Load(string folder)
    {
        string graphPath = Path.Combine(folder, GraphFile);
        string characterPath = Path.Combine(folder, "tables", "Characters.json");
        string ascendancyPath = Path.Combine(folder, "tables", "Ascendancy.json");
        string passivePath = Path.Combine(folder, "tables", "PassiveSkills.json");
        if (!new[] { graphPath, characterPath, ascendancyPath, passivePath }.All(File.Exists)) return [];

        using var graphs = GameTables.Open(graphPath);
        if (graphs.RootElement.GetProperty("revision").GetString() != Revision)
            throw new InvalidDataException($"{graphPath}: expected ascendancy data revision {Revision}");
        using var characters = GameTables.Open(characterPath);
        byte[] baseClasses = characters.RootElement.EnumerateArray().Select(r => checked((byte)r.GetProperty("IntegerId").GetInt32())).ToArray();
        using var ascendancies = GameTables.Open(ascendancyPath);
        using var passives = GameTables.Open(passivePath);
        var rows = new Dictionary<ushort, JsonElement>();
        foreach (var r in passives.RootElement.EnumerateArray())
        {
            int id = r.GetProperty("PassiveSkillGraphId").GetInt32();
            if (id is <= 0 or > ushort.MaxValue) continue;
            if (!rows.TryAdd((ushort)id, r)) throw new InvalidDataException($"{passivePath}: duplicate graph id {id}");
        }

        var definitions = new List<Definition>();
        foreach (var family in Enum.GetValues<AscendancyFamily>())
        {
            var graph = ReadGraph(graphs.RootElement.GetProperty(family == AscendancyFamily.Normal ? "normal" : "phrecia"));
            var names = new HashSet<(byte BaseClass, string Name)>();
            int tableRow = -1;
            foreach (var r in ascendancies.RootElement.EnumerateArray())
            {
                tableRow++;
                int characterRow = r.GetProperty("Character").GetInt32();
                if (characterRow < 0 || characterRow >= baseClasses.Length)
                    throw new InvalidDataException($"{ascendancyPath}: row {tableRow} has invalid character row {characterRow}");
                byte baseClass = baseClasses[characterRow];
                string name = r.GetProperty(family == AscendancyFamily.Normal ? "Name" : "LeagueName").GetString() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var members = new Dictionary<ushort, Node>();
                foreach (var (id, passive) in rows)
                {
                    if (!graph.TryGetValue(id, out var links) || !passive.TryGetProperty("AscendancyKey", out var owner) ||
                        owner.ValueKind != JsonValueKind.Number || owner.GetInt32() != tableRow) continue;
                    var keys = passive.GetProperty("Stats").EnumerateArray().Select(k => k.GetInt32()).ToArray();
                    var stats = new List<(uint Key, int Value)>();
                    for (int i = 0; i < keys.Length && i < 5; i++)
                        stats.Add((checked((uint)keys[i] + 1), passive.GetProperty($"Stat{i + 1}Value").GetInt32()));
                    members.Add(id, new Node(id, passive.GetProperty("Id").GetString() ?? "", passive.GetProperty("Name").GetString() ?? "",
                        links.Order().ToArray(), passive.GetProperty("IsAscendancyStartingNode").GetBoolean(),
                        passive.GetProperty("SkillPointsGranted").GetInt32(), passive.GetProperty("IsMultipleChoice").GetBoolean(),
                        passive.GetProperty("IsMultipleChoiceOption").GetBoolean(), stats));
                }
                if (members.Count == 0) continue; // e.g. the two ordinary-only Scion rows in the Phrecia graph.
                var starts = members.Values.Where(n => n.IsStart).ToArray();
                if (starts.Length != 1)
                    throw new InvalidDataException($"{graphPath}: {family}/{name} has {starts.Length} start nodes, expected one");
                if (!names.Add((baseClass, name)))
                    throw new InvalidDataException($"{graphPath}: duplicate {family} choice {name} for base class {baseClass}");
                // Links to the ordinary tree/start decorations never authorise another ascendancy's nodes.
                foreach (var node in members.Values.ToArray())
                    members[node.Id] = node with { Adjacent = node.Adjacent.Where(members.ContainsKey).ToArray() };
                definitions.Add(new Definition(new(family, r.GetProperty("Id").GetString() ?? "", Revision), name,
                    baseClass, tableRow, checked((byte)r.GetProperty("ClassNo").GetInt32()), starts[0].Id, members));
            }
        }
        return definitions;
    }

    static Dictionary<ushort, HashSet<ushort>> ReadGraph(JsonElement family)
    {
        var result = new Dictionary<ushort, HashSet<ushort>>();
        foreach (var r in family.GetProperty("nodes").EnumerateArray())
        {
            ushort id = checked((ushort)r.GetProperty("id").GetInt32());
            if (!result.TryAdd(id, r.GetProperty("adjacent").EnumerateArray().Select(a => checked((ushort)a.GetInt32())).ToHashSet()))
                throw new InvalidDataException($"duplicate passive graph node {id}");
        }
        // Connectivity is undirected; tolerate generators preserving the PSG's directed storage order.
        foreach (var (id, links) in result.ToArray())
            foreach (ushort other in links.ToArray())
            {
                if (!result.TryGetValue(other, out var reverse)) throw new InvalidDataException($"passive graph node {id} links to missing node {other}");
                reverse.Add(id);
            }
        return result;
    }
}
