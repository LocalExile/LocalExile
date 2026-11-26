using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Core;

/// <summary>An ascendancy family: a tree of main ascendancies. The ordinary one is the game's own; a module supplies any
/// other (<see cref="IAscendancyFamilyPolicy"/>), and its choices are separate from the ordinary ones even when they share
/// a table row and start. The value is a number of this run only (<see cref="AscendancyFamilies.Named"/>): a save keeps
/// the family's name.</summary>
[JsonConverter(typeof(AscendancyFamilies.Json))]
public enum AscendancyFamily { Normal }

/// <summary>The families by name. A name read from a save is a family whether or not its module is loaded, so the save
/// keeps it.</summary>
public static class AscendancyFamilies
{
    static string[] _names = [nameof(AscendancyFamily.Normal)];

    /// <summary>The family of that name (case does not matter), made on first use.</summary>
    public static AscendancyFamily Named(string name)
    {
        while (true)
        {
            var names = Volatile.Read(ref _names);
            int at = Array.FindIndex(names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) return (AscendancyFamily)at;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _names, [.. names, name], names), names)) return (AscendancyFamily)names.Length;
        }
    }

    /// <summary>A family already named (the ordinary one, a loaded module's, a save's): for text a player typed.</summary>
    public static bool TryNamed(string name, out AscendancyFamily family)
    {
        int at = Array.FindIndex(Volatile.Read(ref _names), n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        family = (AscendancyFamily)Math.Max(at, 0);
        return at >= 0;
    }

    public static string Name(this AscendancyFamily family) =>
        Volatile.Read(ref _names) is var names && (uint)family < (uint)names.Length ? names[(int)family] : ((int)family).ToString();

    public sealed class Json : JsonConverter<AscendancyFamily>
    {
        // A number is read as before for the ordinary family (a build converter writes 0); another family's number
        // means nothing outside the run that gave it, so it is refused.
        public override AscendancyFamily Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Number
                ? reader.GetInt32() == 0 ? AscendancyFamily.Normal
                    : throw new JsonException("An ascendancy family other than the ordinary one is given by its name")
                : reader.GetString() is { Length: > 0 } name ? Named(name) : AscendancyFamily.Normal;
        public override void Write(Utf8JsonWriter writer, AscendancyFamily value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Name());
    }
}

/// <summary>A family's tree: its choices, and the links of its whole passive graph (the ordinary passives with its
/// ascendancies), undirected.</summary>
public sealed record AscendancyTree(IReadOnlyList<AscendancyCatalog.Definition> Definitions, Dictionary<ushort, HashSet<ushort>> Links);

/// <summary>A durable choice. Table rows and the client's packed class byte are deliberately not save identifiers.</summary>
public sealed record AscendancyIdentity(AscendancyFamily Family, string Id, string Revision)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>
/// Main ascendancies in the exact extracted client: the ordinary family's, and those of the families whose modules are
/// loaded. Membership and links come from a family's passive graph; the table alone holds every family's rows and is
/// insufficient to authorise an allocation.
/// </summary>
public sealed class AscendancyCatalog
{
    readonly string folder;

    public AscendancyCatalog(string folder = "data")
    {
        this.folder = folder;
        _definitions = Load(folder);
        _members = PassiveGraphs.Available ? [.. PassiveGraphs.Nodes(NormalGraph).Select(n => checked((ushort)n.Int("id")))] : null;
    }

    public const string Revision = "3.29.3c";
    /// <summary>The ordinary tree's name in passive_graphs.</summary>
    public const string NormalGraph = "normal";

    /// <summary>Revisions whose reviewed passive graph is byte-identical to <see cref="Revision"/>. A save written by
    /// one of them keeps its ascendancy choice; add a revision only after its graph has been compared and reviewed.</summary>
    static readonly string[] EquivalentRevisions = ["3.29.3b"];

    public static bool IsKnownRevision(string? revision) =>
        revision == Revision || EquivalentRevisions.Contains(revision);

    /// <summary>Same family and id on a known equivalent graph revision. Used by saved selections and by the select
    /// transition, so a 3.29.3b save does not have to refund an unchanged ascendancy after the 3.29.3c move.</summary>
    public static bool SameChoice(AscendancyIdentity? a, AscendancyIdentity? b) =>
        a is not null && b is not null && a.Family == b.Family && a.Id == b.Id
        && IsKnownRevision(a.Revision) && IsKnownRevision(b.Revision);

    public sealed record Node(ushort Id, string PassiveId, string Name, IReadOnlyList<ushort> Adjacent,
        bool IsStart, int PointsGranted, bool IsChoice, bool IsChoiceOption,
        IReadOnlyList<(uint Key, int Value)> Stats)
    {
        // A multiple-choice option is the effect of the paid parent, not a second ascendancy point.
        public int PointCost => IsStart || IsChoiceOption ? 0 : 1;

        // Refund currency is a different calculation (client 0x141f01950): the choice shell is free to remove;
        // changing its selected effect costs five regrets, although that effect consumes no extra ascendancy point.
        public int RefundCost => IsStart || IsChoice ? 0 : AscendancyRules.RefundPointsPerAscendancyPoint;
    }

    public sealed record Definition(AscendancyIdentity Identity, string Name, byte BaseClass, int TableRow,
        byte ClassNo, ushort StartNode, IReadOnlyDictionary<ushort, Node> Nodes);

    readonly IReadOnlyList<Definition> _definitions;
    readonly HashSet<ushort>? _members;
    Tuple<AscendancyTree[], IReadOnlyList<Definition>>? _all;

    /// <summary>The ordinary choices, then those of the loaded families.</summary>
    public IReadOnlyList<Definition> Definitions
    {
        get
        {
            var trees = AscendancyPolicies.Trees;
            if (trees.Length == 0) return _definitions;
            var all = _all;
            if (all is null || !ReferenceEquals(all.Item1, trees))
                _all = all = new(trees, [.. _definitions, .. trees.SelectMany(t => t.Definitions)]);
            return all.Item2;
        }
    }
    public bool Loaded => _definitions.Count > 0;
    public bool GraphLoaded => _members is not null;
    public IEnumerable<Definition> Choices(byte baseClass) => Definitions.Where(d => d.BaseClass == baseClass);

    /// <summary>Never keep/apply a node that the chosen client graph cannot display. This check also protects old saves
    /// when a graph revision changes; the caller refuses the change/load and retains the original save.</summary>
    public void ValidateOrdinaryNodes(AscendancyFamily family, IEnumerable<ushort> nodes)
    {
        var links = family == AscendancyFamily.Normal ? null : AscendancyPolicies.Tree(family)?.Links;
        if (family == AscendancyFamily.Normal ? _members is null : links is null)
            throw new AscendancyRuleException(family == AscendancyFamily.Normal ? "Passive graph data is missing; run scripts/dev/extract-data.ps1"
                : $"The {family.Name()} passive graph is not there: its module is not loaded");
        foreach (ushort id in nodes)
            if (!(links?.ContainsKey(id) ?? _members!.Contains(id)))
                throw new AscendancyRuleException($"Ordinary passive {id} is absent from the {family.Name()} client graph; refund it before changing families");
    }

    public Definition? Find(AscendancyIdentity identity) => Definitions.FirstOrDefault(d => SameChoice(d.Identity, identity));

    /// <summary>Names (including spaces) and stable table Ids accepted by the local selection command.</summary>
    public Definition? Find(byte baseClass, AscendancyFamily family, string name) => Choices(baseClass).FirstOrDefault(d =>
        d.Identity.Family == family && (string.Equals(d.Identity.Id, name, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)));

    static IReadOnlyList<Definition> Load(string folder)
    {
        if (!PassiveGraphs.Available || !new[] { "Characters", "Ascendancy", "PassiveSkills" }.All(ClientTables.Has)) return [];
        PassiveGraphs.RequireRevision(Revision, "ascendancy");
        return Build(AscendancyFamily.Normal, "Name", PassiveGraphs.Links(NormalGraph));
    }

    /// <summary>A family's choices: the Ascendancy rows that have a name in <paramref name="nameColumn"/> and passives in
    /// <paramref name="graph"/>, the family's passive graph. A module builds its family's with it.</summary>
    public static IReadOnlyList<Definition> Build(AscendancyFamily family, string nameColumn, IReadOnlyDictionary<ushort, HashSet<ushort>> graph)
    {
        var characters = ClientTables.Table("Characters");
        byte[] baseClasses = characters.Select(r => checked((byte)r.Int("IntegerId"))).ToArray();
        var ascendancies = ClientTables.Table("Ascendancy");
        var passives = ClientTables.Table("PassiveSkills");
        var rows = new Dictionary<ushort, ClientRow>();
        foreach (var r in passives)
        {
            int id = r.Int("PassiveSkillGraphId");
            if (id is <= 0 or > ushort.MaxValue) continue;
            if (!rows.TryAdd((ushort)id, r)) throw new InvalidDataException($"PassiveSkills: duplicate graph id {id}");
        }

        var definitions = new List<Definition>();
        {
            var names = new HashSet<(byte BaseClass, string Name)>();
            int tableRow = -1;
            foreach (var r in ascendancies)
            {
                tableRow++;
                int characterRow = r.Int("Character");
                if (characterRow < 0 || characterRow >= baseClasses.Length)
                    throw new InvalidDataException($"Ascendancy: row {tableRow} has invalid character row {characterRow}");
                byte baseClass = baseClasses[characterRow];
                string name = r.Text(nameColumn) ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var members = new Dictionary<ushort, Node>();
                foreach (var (id, passive) in rows)
                {
                    if (!graph.TryGetValue(id, out var links) || passive.IntOrNull("AscendancyKey") != tableRow) continue;
                    var keys = passive.Ints("Stats");
                    var stats = new List<(uint Key, int Value)>();
                    for (int i = 0; i < keys.Length && i < 5; i++)
                        stats.Add((checked((uint)keys[i] + 1), passive.Int($"Stat{i + 1}Value")));
                    members.Add(id, new Node(id, passive.Text("Id") ?? "", passive.Text("Name") ?? "",
                        links.Order().ToArray(), passive.Bool("IsAscendancyStartingNode"),
                        passive.Int("SkillPointsGranted"), passive.Bool("IsMultipleChoice"),
                        passive.Bool("IsMultipleChoiceOption"), stats));
                }
                if (members.Count == 0) continue; // a row with no passive in this family's graph
                var starts = members.Values.Where(n => n.IsStart).ToArray();
                if (starts.Length != 1)
                    throw new InvalidDataException($"passive_graphs: {family.Name()}/{name} has {starts.Length} start nodes, expected one");
                if (!names.Add((baseClass, name)))
                    throw new InvalidDataException($"passive_graphs: duplicate {family.Name()} choice {name} for base class {baseClass}");
                // Links to the ordinary tree/start decorations never authorise another ascendancy's nodes.
                foreach (var node in members.Values.ToArray())
                    members[node.Id] = node with { Adjacent = node.Adjacent.Where(members.ContainsKey).ToArray() };
                definitions.Add(new Definition(new(family, r.Text("Id") ?? "", Revision), name,
                    baseClass, tableRow, checked((byte)r.Int("ClassNo")), starts[0].Id, members));
            }
        }
        return definitions;
    }
}
