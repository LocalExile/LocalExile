using GameServer.Core.Protocol.Instance;
using System.Text.Json;


/// <summary>
/// The client's cluster jewel layout: which node sits on each position of the id block it numbers for a pane cell.
///
/// Built from <c>PassiveTreeExpansionJewels.dat</c> (the template lists of smalls, notables and sockets per size),
/// <c>PassiveJewelSlots.dat</c>'s <c>StartIndices</c> and the client's own node-id function (Ghidra <c>FUN_141f0e770</c>,
/// hooked live with Frida):
/// <list type="bullet">
/// <item>the block base is <c>39 * ClusterIndex</c> for a jewel in a tree socket (cell 20, index 5, uses 195), a child
/// of one adds <c>9 * its ClusterIndex + 12</c> (cell 51 -> 207, cell 53 -> 225) and a grandchild adds 6;</item>
/// <item>a tree-socket large jewel uses the template positions: <c>id = base + (start + templateIndex) % total</c>;</item>
/// <item>a jewel in an expansion cell is compressed onto half the ring (its nodes are drawn on the client's 16-slot
/// orbit, and <c>id = base + position / 2</c>): a 4-passive medium sits at indices 0, 3, 6, 9, its socket on the
/// template's socket index (6) and the others small passives.</item>
/// </list>
///
/// Measured with <c>scripts/re/frida/trace-cluster-ids.js</c> on the live client: a large 8/2 in cell 20 gives
/// 195..206 (socket 206/198, notables 196/200/204 and smalls 195/197/202), a medium 4/1 in cell 51 gives
/// 208/209/211 (smalls) and 212 (socket), the same medium in cell 53 gives 225/228/229 and 226.
/// </summary>
static class ClusterJewelLayout
{
    public enum Kind { None, Small, Notable, Socket }

    public sealed record Layout(int Base, int Total, Dictionary<int, Kind> ByOffset, Dictionary<int, int> NotableOrders);

    sealed record Template(int[] Small, int[] Notable, int[] Socket, int Total);

    static readonly Lazy<Dictionary<int, Template>> Templates = new(() =>
    {
        var templates = new Dictionary<int, Template>();
        string path = Path.Combine("data", "tables", "PassiveTreeExpansionJewels.json");
        if (!File.Exists(path)) return templates;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var row in document.RootElement.EnumerateArray())
        {
            int size = row.GetProperty("PassiveTreeExpansionJewelSizesKey").GetInt32();
            templates[size] = new(
                [.. row.GetProperty("SmallIndices").EnumerateArray().Select(v => v.GetInt32())],
                [.. row.GetProperty("NotableIndices").EnumerateArray().Select(v => v.GetInt32())],
                [.. row.GetProperty("SocketIndices").EnumerateArray().Select(v => v.GetInt32())],
                row.GetProperty("TotalIndices").GetInt32());
        }
        return templates;
    });

    /// <summary>The cell's id block base: the six tree sockets take <c>39 * ClusterIndex</c> (cell 20, index 5, uses
    /// 195), a child of a tree socket adds <c>9 * its ClusterIndex + 12</c> (cell 51 -> 207, cell 53 -> 225) and a
    /// grandchild adds 6.</summary>
    static int? Base(int cell)
    {
        if (Program.Passives.IsLargeJewelSlot(cell)) return 39 * Program.Passives.ClusterIndex(cell);
        if (Program.Passives.ClusterParent(cell) is not int parent) return null;
        if (Program.Passives.IsLargeJewelSlot(parent))
            return 39 * Program.Passives.ClusterIndex(parent) + 9 * Program.Passives.ClusterIndex(cell) + 12;
        return Base(parent) is int up ? up + 6 : null;
    }

    /// <summary>The layout of the cluster jewel in this pane cell, or null when the cell's block is not known or the
    /// jewel's shape is not measured yet (the caller falls back to the count-based estimate).</summary>
    public static Layout? For(int cell, Item jewel)
    {
        if (Game.ClusterSize(jewel) is not int size || !Templates.Value.TryGetValue(size, out var template)) return null;
        // The jewel keeps its own template; the cell only supplies the block base and whether the ring is compressed.
        // A smaller jewel in a larger cell is padded by the client with empty nodes, but its own positions are the
        // item's (measured: a small in the large tree socket 17 uses the 6-ring indices 0/2/4 with the cell's start 5,
        // so its first notable is 159 and its two theme smalls 157/161).
        int cellSize = Program.Passives.ClusterCellSize(cell);
        if (cellSize < 0) return null;
        if (Base(cell) is not int block) return null;
        var starts = Program.Passives.ClusterStartIndices(cell);
        int start = size < starts.Count ? starts[size] : 0;
        int total = template.Total;
        if (total <= 0) return null;

        var entries = ClusterJewelStats.Entries(jewel);
        int sockets = Math.Min(Game.ClusterSocketCount(jewel), template.Socket.Length);
        int notables = entries.Count(e => e.Stat.StartsWith("local_affliction_notable_", StringComparison.Ordinal));
        int keystones = entries.Count(e => e.Stat.StartsWith("local_jewel_expansion_keystone_", StringComparison.Ordinal));
        int nodes = Game.ClusterNodeBudget(jewel);
        bool treeSocket = Program.Passives.IsLargeJewelSlot(cell);
        // Only a large cell (a tree socket) keeps the full 12-slot ring; the expansion cells are compressed onto half
        // of it, whatever the jewel's own size.
        bool compressed = cellSize != 2;

        var kinds = new Dictionary<int, Kind>();
        var orders = new Dictionary<int, int>();
        void Put(int index, Kind kind, int order = -1)
        {
            int position = ((index + start) % total + total) % total;
            int offset = compressed ? position / 2 : position;
            kinds[offset] = kind;
            if (order >= 0) orders[offset] = order;
        }

        if (treeSocket)
        {
            // The client's template layout: sockets take the first socket positions, the notables the first free notable
            // positions in ascending order (the item's notables in the client's order), the smalls the remaining list.
            var used = new HashSet<int>();
            for (int i = 0; i < sockets; i++)
            {
                used.Add(template.Socket[i]);
                Put(template.Socket[i], Kind.Socket);
            }
            var notablePositions = template.Notable.Where(i => !used.Contains(i)).Take(notables).Order().ToArray();
            for (int i = 0; i < notablePositions.Length; i++)
            {
                used.Add(notablePositions[i]);
                Put(notablePositions[i], Kind.Notable, i);
            }
            // The template has a limited number of notable positions: a smaller jewel's extra notable mods are inert,
            // and its remaining nodes are theme smalls.
            int smalls = Math.Max(0, Game.ClusterNodeBudget(jewel) - sockets - notablePositions.Length - keystones);
            foreach (int index in template.Small.Where(i => !used.Contains(i)).Take(smalls))
                Put(index, Kind.Small);
        }
        else
        {
            // An expansion cell's nodes are laid evenly around the ring (a 4-node medium uses 0, 3, 6, 9) and the
            // jewel's own socket takes the template's socket index. A jewel with notables or keystones has not been
            // measured in an expansion cell yet, so it stays on the count-based estimate.
            if (keystones > 0 || nodes <= 0 || total % nodes != 0) return null;
            int step = total / nodes;
            var ring = Enumerable.Range(0, nodes).Select(i => i * step).ToArray();
            // The jewel's own socket takes the template's socket index, its first notable the template's notable index.
            // A small cluster has a single notable position (index 4), so with several notable mods the client shows
            // only the first one and the other two positions are theme smalls; extra notable mods are inert.
            int socketIndex = sockets > 0 ? template.Socket[0] : -1;
            int notableIndex = notables > 0 ? template.Notable[0] : -1;
            if (socketIndex >= 0 && !ring.Contains(socketIndex)) return null;
            if (notableIndex >= 0 && !ring.Contains(notableIndex)) return null;
            if (socketIndex >= 0 && notableIndex >= 0 && socketIndex == notableIndex) return null;
            foreach (int index in ring)
            {
                if (index == notableIndex) Put(index, Kind.Notable, 0);
                else if (index == socketIndex) Put(index, Kind.Socket);
                else Put(index, Kind.Small);
            }
        }

        return new Layout(block, compressed ? total / 2 : total, kinds, orders);
    }
}
