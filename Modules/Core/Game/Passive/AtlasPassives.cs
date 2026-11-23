using System.Text.Json;
using GameServer.Core;


/// <summary>
/// The 3.29 Atlas passive tree's nodes. PassiveSkills.dat holds the Atlas nodes next to the character's tree; their Ids
/// start with <c>atlas_</c>. The client supplies the tree's geometry and connectivity, so the server needs the node ids
/// (PassiveSkillGraphId) for the S→C 0x3d preset lists and the C→S 0x3e/0x3f allocation requests. A HardMode catalog
/// holds the HardMode stat vectors (PassiveTree's rule: a row with HasHardmodeAlternate replaces its whole vector).
/// </summary>
public sealed class AtlasPassives
{
    readonly string folder;

    public AtlasPassives(string folder = "data", bool hardMode = false)
    {
        this.folder = folder;
        _nodes = LoadNodes(folder, hardMode);
    }

    public sealed record Node(ushort GraphId, string Id, string Name, bool IsNotable, bool IsKeystone,
        IReadOnlyList<(uint Key, int Value)> Stats, bool IsJustIcon = false, int SkillPoints = 0, int SkillPointsHardMode = 0,
        bool IsWormhole = false);

    readonly Dictionary<ushort, Node> _nodes;

    public IReadOnlyCollection<Node> Nodes => _nodes.Values;

    public int Count => _nodes.Count;

    public bool Loaded => _nodes.Count > 0;

    readonly Dictionary<ushort, HashSet<ushort>> _links =
        PassiveGraphs.Available && PassiveGraphs.Graph("atlas") is not null ? PassiveGraphs.Links("atlas") : [];

    /// <summary>The Atlas graph's undirected links by node (client.db passive_graph_nodes, graph "atlas"); empty when the
    /// database has no Atlas graph.</summary>
    public IReadOnlyDictionary<ushort, HashSet<ushort>> Links => _links;

    /// <summary>The graph's one root, the start node.</summary>
    public const ushort StartGraphId = 29045;

    /// <summary>Whether the player may allocate <paramref name="node"/> on top of <paramref name="allocated"/>: it is on the
    /// graph, is not the start, and touches the start or an allocated node that is reached from the start (the graph's edges have no direction).</summary>
    public static bool CanAllocate(IReadOnlyCollection<ushort> allocated, ushort node,
        IReadOnlyDictionary<ushort, HashSet<ushort>> links, ushort start)
    {
        if (node == start || !links.TryGetValue(node, out var next)) return false;
        if (next.Contains(start)) return true;
        var reached = Reached(allocated, links, start);
        return next.Any(reached.Contains);
    }

    /// <summary>Whether the player may refund <paramref name="node"/>: no other allocated node may lose its path from the
    /// start through allocated nodes (a node already cut off, such as one an old save holds, does not block).</summary>
    public static bool CanRefund(IReadOnlyCollection<ushort> allocated, ushort node,
        IReadOnlyDictionary<ushort, HashSet<ushort>> links, ushort start)
    {
        var reached = Reached(allocated, links, start);
        var after = Reached(allocated.Where(n => n != node).ToList(), links, start);
        return after.Count == reached.Count - (reached.Contains(node) ? 1 : 0);
    }

    /// <summary>Whether allocated <paramref name="node"/> is reached from the start through allocated nodes.</summary>
    public static bool IsReached(IReadOnlyCollection<ushort> allocated, ushort node,
        IReadOnlyDictionary<ushort, HashSet<ushort>> links, ushort start) =>
        Reached(allocated, links, start).Contains(node);

    static HashSet<ushort> Reached(IReadOnlyCollection<ushort> allocated, IReadOnlyDictionary<ushort, HashSet<ushort>> links,
        ushort start)
    {
        var rest = allocated.ToHashSet();
        var seen = new HashSet<ushort>();
        var open = new Stack<ushort>();
        open.Push(start);
        while (open.Count > 0)
            if (links.TryGetValue(open.Pop(), out var next))
                foreach (ushort n in next)
                    if (rest.Contains(n) && seen.Add(n)) open.Push(n);
        return seen;
    }

    public Node? Find(ushort graphId) => _nodes.GetValueOrDefault(graphId);

    /// <summary>A preset's point budget: the character's points plus what its allocated nodes grant (SkillPointsGranted,
    /// the Hardmode column in a HardMode league; the client counts the same, 0x141ef4840).</summary>
    public int Budget(int points, IEnumerable<ushort> allocated, bool hardMode) =>
        points + allocated.Sum(n => Find(n) is { } node ? hardMode ? node.SkillPointsHardMode : node.SkillPoints : 0);

    public Node? FindById(string id) => _nodes.Values.FirstOrDefault(n => n.Id == id);

    static Dictionary<ushort, Node> LoadNodes(string folder, bool hardMode)
    {
        var nodes = new Dictionary<ushort, Node>();
        if (!ClientTables.Has("PassiveSkills")) return nodes;
        foreach (var row in ClientTables.Table("PassiveSkills"))
        {
            string id = row.Text("Id") ?? "";
            if (!id.StartsWith("atlas_", StringComparison.Ordinal)) continue;
            int graphId = row.Int("PassiveSkillGraphId");
            if (graphId is <= 0 or > ushort.MaxValue) continue;
            bool alternate = hardMode && row.BoolOrNull("HasHardmodeAlternate") == true;
            var stats = new List<(uint Key, int Value)>();
            var keys = row.Ints(alternate ? "StatsHardmode" : "Stats").ToList();
            for (int i = 0; i < keys.Count && i < 5; i++)
                stats.Add(((uint)keys[i] + 1, row.Int($"Stat{i + 1}Value{(alternate ? "Hardmode" : "")}")));
            nodes.TryAdd((ushort)graphId, new Node((ushort)graphId, id, row.Text("Name") ?? "",
                row.Bool("IsNotable"), row.Bool("IsKeystone"), stats, row.Bool("IsJustIcon"), row.Int("SkillPointsGranted"),
                row.Int(row.BoolOrNull("HasHardmodeAlternate") == true ? "SkillPointsGrantedHardmode" : "SkillPointsGranted"),
                row.Bool("IsAtlasWormhole")));
        }
        return nodes;
    }
}
