using GameServer.Core.Protocol.Instance;
using System.Text.Json;
using GameServer.Core;


/// <summary>
/// The client's cluster jewel layout: which node sits on each position of the id block it numbers for a pane cell.
///
/// Built from <c>PassiveTreeExpansionJewels.dat</c> (the template lists of smalls, notables and sockets per size),
/// <c>PassiveJewelSlots.dat</c>'s <c>StartIndices</c> and the client's own node-id function (Ghidra <c>FUN_141f15060</c>,
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
public static class ClusterJewelLayout
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadClusterJewelLayoutData()
    {
        Templates = LoadTemplates();
    }

    public enum Kind { None, Small, Notable, Socket, Keystone }

    public sealed record Layout(int Base, int Total, Dictionary<int, Kind> ByOffset, Dictionary<int, int> NotableOrders)
    {
        public int EntryOffset { get; init; }
        public IReadOnlyDictionary<int, int> ChildSockets { get; init; } = new Dictionary<int, int>();
    }

    sealed record Template(int[] Small, int[] Notable, int[] Socket, int Total);

    static Dictionary<int, Template> Templates = default!;

    static Dictionary<int, Template> LoadTemplates()
    {
        var templates = new Dictionary<int, Template>();
        if (!ClientTables.Has("PassiveTreeExpansionJewels")) return templates;
        foreach (var row in ClientTables.Table("PassiveTreeExpansionJewels"))
        {
            int size = row.Int("PassiveTreeExpansionJewelSizesKey");
            templates[size] = new(
                [.. row.Ints("SmallIndices")],
                [.. row.Ints("NotableIndices")],
                [.. row.Ints("SocketIndices")],
                row.Int("TotalIndices"));
        }
        return templates;
    }

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
        if (Game.ClusterSize(jewel) is not int size || !Templates.TryGetValue(size, out var template)) return null;
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
        int notables = entries.Count(e => Program.Stats.Name(e.Stat).StartsWith("local_affliction_notable_", StringComparison.Ordinal));
        int keystones = entries.Count(e => Program.Stats.Name(e.Stat).StartsWith("local_jewel_expansion_keystone_", StringComparison.Ordinal));
        int nodes = Game.ClusterNodeBudget(jewel);
        // Only a large cell (a tree socket) keeps the full 12-slot ring; the expansion cells are compressed onto half
        // of it, whatever the jewel's own size.
        bool compressed = cellSize != 2;

        int Offset(int index)
        {
            int position = ((index + start) % total + total) % total;
            return compressed ? position / 2 : position;
        }
        // A single-keystone small cluster creates its only node at index zero, rotated by the cell start.
        if (size == 0 && keystones == 1 && nodes == 1)
            return new Layout(block, compressed ? total / 2 : total, new() { [Offset(0)] = Kind.Keystone }, [])
                { EntryOffset = Offset(0) };

        var kinds = new Dictionary<int, Kind>();
        var orders = new Dictionary<int, int>();
        void Put(int index, Kind kind, int order = -1)
        {
            int offset = Offset(index);
            kinds[offset] = kind;
            if (order >= 0) orders[offset] = order;
        }

        // Medium clusters use the template priorities with the client's four/five-node remapping. In particular,
        // a five-node medium without notables still has a real ring and can bridge a Split Personality socket.
        int NotablePosition(int index) => size != 1 ? index
            : sockets == 0 && notables == 2 ? index switch { 6 => 4, 10 => 8, _ => index }
            : nodes == 4 ? index switch { 10 => 9, 2 => 3, _ => index } : index;
        int SmallPosition(int index) => size != 1 ? index
            : nodes == 4 ? index switch { 8 => 9, 4 => 3, _ => index }
            : nodes == 5 && index == 4 ? 3 : index;
        var used = new HashSet<int>();
        for (int i = 0; i < sockets; i++)
        {
            used.Add(template.Socket[i]);
            Put(template.Socket[i], Kind.Socket);
        }
        var notablePositions = template.Notable.Select(NotablePosition).Where(i => !used.Contains(i))
            .Take(notables).Order().ToArray();
        for (int i = 0; i < notablePositions.Length; i++)
        {
            used.Add(notablePositions[i]);
            Put(notablePositions[i], Kind.Notable, i);
        }
        int smalls = Math.Max(0, nodes - sockets - notablePositions.Length - keystones);
        foreach (int index in template.Small.Select(SmallPosition).Where(i => !used.Contains(i)).Take(smalls))
            Put(index, Kind.Small);

        var children = new Dictionary<int, int>();
        foreach (int child in Program.Passives.ClusterSlots(cell))
        {
            int index = Program.Passives.ClusterIndex(child);
            // Large jewels open the two outer sockets before the middle one. Pane indexes run left/middle/right,
            // while PassiveTreeExpansionJewels.SocketIndices is ordered left/right/middle (4,8,6).
            int order = size == 2 ? index switch { 1 => 2, 2 => 1, _ => index } : index;
            if (order >= 0 && order < sockets && order < template.Socket.Length)
                children[child] = Offset(template.Socket[order]);
        }
        return new Layout(block, compressed ? total / 2 : total, kinds, orders)
            { EntryOffset = Offset(0), ChildSockets = children };
    }
}
