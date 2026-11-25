using GameServer.Core;

/// <summary>
/// The client's passive skill graphs (its PSG files, by name: "normal", "atlas" and what else was extracted), as client.db's passive_graphs,
/// passive_graph_nodes, passive_graph_groups, passive_graph_group_nodes and passive_graphs_info (data/
/// passive-skill-graphs.json from scripts/re/passive-graphs).
/// </summary>
public static class PassiveGraphs
{
    /// <summary>Whether client.db has the graphs.</summary>
    public static bool Available => ClientTables.Has("passive_graphs") && ClientTables.Has("passive_graph_nodes");

    /// <summary>The client revision the graphs were extracted from; throws unless it is <paramref name="expected"/>.</summary>
    public static void RequireRevision(string expected, string what)
    {
        string? revision = ClientTables.TableOrNull("passive_graphs_info") is { Count: > 0 } info ? info[0].TextOrNull("revision") : null;
        if (revision != expected)
            throw new InvalidDataException($"passive_graphs_info: expected {what} data revision {expected}, found {revision ?? "none"}");
    }

    /// <summary>The named graph's row in passive_graphs, or null.</summary>
    public static ClientRow? Graph(string name)
    {
        foreach (var g in ClientTables.TableOrEmpty("passive_graphs"))
            if (g.Text("graph") == name) return g;
        return null;
    }

    /// <summary>The named graph's nodes in graph order.</summary>
    public static IEnumerable<ClientRow> Nodes(string name) =>
        Graph(name) is { } g ? ClientTables.Table("passive_graph_nodes").By("graph")[g.Index] : [];

    /// <summary>The named graph's links, undirected: the PSG stores a drawing edge in either direction.</summary>
    public static Dictionary<ushort, HashSet<ushort>> Links(string name)
    {
        var result = new Dictionary<ushort, HashSet<ushort>>();
        foreach (var node in Nodes(name))
        {
            ushort id = checked((ushort)node.Int("id"));
            if (!result.TryAdd(id, node.Ints("adjacent").Select(n => checked((ushort)n)).ToHashSet()))
                throw new InvalidDataException($"Duplicate passive graph node {id}");
        }
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
