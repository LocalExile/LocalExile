namespace GameServer.Data.Terrain.Generation;

/// <summary>The AzmeriPlugin's part in the generation (the Viridian Wildwood beside a map with Affliction content).
/// The plugin queues the woods' world area (AzmeriLeagueArea: <c>woods_0N.tgr</c>, 48 × 48 plus its buffer, a primary
/// record packed beside the root like the Harvest garden; <c>0x141f2f530</c>) and, when that graph is read, its hook
/// (<c>0x141f2f960</c>, for a record whose area kind +0x1e4 is 47) retypes the graph's "clearing" nodes from the area
/// load's blob (<c>0x141f2fcd0</c>: 4 × u8 AzmeriFeatureRooms row, then a flag byte, IntroTunnel): the clearing nodes
/// are collected in node order and slot k retypes the one taken FROM THE END (slot 0 = the last) to the row's room
/// (+8, its .arm tag) and appends "accessible" and the row's +0x10 tokens to the node's descriptions; the loop stops
/// when the clearings run out. The corpus's sim matched all six woods loads with this rule.</summary>
public sealed class AzmeriWoods(IReadOnlyList<int> rows, int flag)
{
    public int Flag => flag;

    public static bool IsWoodsGraph(string graphPath) =>
        graphPath.Replace('\\', '/').Contains("/Leagues/Azmeri/AzmeriWoods/Graphs/", StringComparison.OrdinalIgnoreCase);

    public void Apply(TileGraph graph, TerrainCache files, List<string> notes)
    {
        var table = new RawDat(files.Raw("Data/AzmeriFeatureRooms.datc64") ?? throw new FileNotFoundException("Data/AzmeriFeatureRooms.datc64"));
        var clearings = graph.Nodes.Where(n => n.Tag == "clearing").ToList();
        for (int k = 0; k < rows.Count && k < clearings.Count; k++)
        {
            int row = rows[k];
            if (row < 0 || row >= table.Count) continue;
            string arm = table.String(row, 8);
            if (arm.Length == 0) continue;
            var room = files.Room(arm) ?? throw new FileNotFoundException(arm);
            var node = clearings[^(k + 1)];
            node.Tag = room.Tag;
            node.Descriptions.Add("accessible");
            node.Descriptions.AddRange(table.String(row, 16).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            notes.Add($"azmeri slot {k}: clearing node ({node.X}, {node.Y}) -> row {row} {room.Tag}");
        }
    }
}
