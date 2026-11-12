using System.Text.Json;
using GameServer.Core;


/// <summary>
/// The 3.29 Atlas passive tree's nodes. PassiveSkills.dat holds the Atlas nodes next to the character's tree; their Ids
/// start with <c>atlas_</c>. The client supplies the tree's geometry and connectivity, so the server needs the node ids
/// (PassiveSkillGraphId) for the S→C 0x3d preset lists and the C→S 0x3e/0x3f allocation requests.
/// </summary>
public sealed class AtlasPassives(string folder = "data")
{
    public sealed record Node(ushort GraphId, string Id, string Name, bool IsNotable, bool IsKeystone,
        IReadOnlyList<(uint Key, int Value)> Stats);

    readonly Lazy<Dictionary<ushort, Node>> _nodes = new(() => LoadNodes(folder));

    public IReadOnlyCollection<Node> Nodes => _nodes.Value.Values;

    public int Count => _nodes.Value.Count;

    public bool Loaded => _nodes.Value.Count > 0;

    public Node? Find(ushort graphId) => _nodes.Value.GetValueOrDefault(graphId);

    public Node? FindById(string id) => _nodes.Value.Values.FirstOrDefault(n => n.Id == id);

    static Dictionary<ushort, Node> LoadNodes(string folder)
    {
        var nodes = new Dictionary<ushort, Node>();
        if (!ClientTables.Has("PassiveSkills")) return nodes;
        foreach (var row in ClientTables.Table("PassiveSkills"))
        {
            string id = row.Text("Id") ?? "";
            if (!id.StartsWith("atlas_", StringComparison.Ordinal)) continue;
            int graphId = row.Int("PassiveSkillGraphId");
            if (graphId is <= 0 or > ushort.MaxValue) continue;
            var stats = new List<(uint Key, int Value)>();
            var keys = row.Ints("Stats").ToList();
            for (int i = 0; i < keys.Count && i < 5; i++)
                stats.Add(((uint)keys[i] + 1, row.Int($"Stat{i + 1}Value")));
            nodes.TryAdd((ushort)graphId, new Node((ushort)graphId, id, row.Text("Name") ?? "",
                row.Bool("IsNotable"), row.Bool("IsKeystone"), stats));
        }
        return nodes;
    }
}
