using GameServer.Core;

namespace GameServer.Modules.Blight;

/// <summary>
/// The lane tree of a pump defence: a BlightTopologies row names a root BlightTopologyNodes row, and each node splits into branches.
/// The table's columns are unnamed; their meaning is read from the rows (estimated, not documented by the client):
/// Topologies <c>__col2</c>/<c>__col3</c> the area level range, <c>__col4</c> the weight, <c>__col6</c> how many copies of the tree
/// stand round the pump. Nodes: <c>Size</c> branches spread over <c>Angle</c> degrees; per branch (a shorter list repeats its last value)
/// <c>__col4</c>/<c>__col5</c> the length range in cells, <c>__col7</c>/<c>__col8</c> the delay range in milliseconds, <c>__col9</c> its
/// foundations; <c>__col1</c> the child node of each branch, the rest being lane ends.
/// </summary>
public static class BlightLayout
{
    public sealed record Topology(int Row, string Id, int Root, int MinLevel, int MaxLevel, int Weight, int Copies);

    public sealed record Node(int Row, string Id, IReadOnlyList<int> Children, int Size, int Angle, IReadOnlyList<int> MinLength,
        IReadOnlyList<int> MaxLength, IReadOnlyList<int> MinDelay, IReadOnlyList<int> MaxDelay, IReadOnlyList<int> Foundations);

    /// <summary>One lane: its cells from the pump out to its far end (about <see cref="Step"/> cells apart) and the seconds after the
    /// waves begin before its first monster.</summary>
    public sealed record Lane(IReadOnlyList<(int X, int Y)> Path, double Delay)
    {
        public (int X, int Y) End => Path[^1];
    }

    public sealed record Plan(string Topology, IReadOnlyList<Lane> Lanes, IReadOnlyList<(int X, int Y)> Foundations);

    /// <summary>How far beside its lane a foundation stands: the median distance (10.05 cells) of the 11 foundations of the
    /// official defence that stand within 25 cells of an enemy's walk.</summary>
    public const int FoundationOffset = 10;

    /// <summary>A foundation keeps this many cells from the pump and from every other foundation, and from a lane's cells [estimated:
    /// the objects' size is 3].</summary>
    public const int PumpGap = 6, FoundationGap = 5, LaneClearance = 3;

    const int MaxDepth = 12;
    const double Step = 2;

    /// <summary>
    /// The nodes. <c>__col1</c> holds 8-byte row keys the export read as 4-byte ints: a list of N keys shows its first N/2 as
    /// "key, 0" pairs. The keys that do not show are the rows no shown key names, by their Ids (the shown key's Id with its last
    /// number counted up, or with "_2" or "2" added); where none is free the branch repeats the last key [estimated].
    /// </summary>
    public static IReadOnlyList<Node> ReadNodes(ClientTable table, IReadOnlyCollection<int> roots)
    {
        var rows = table.ToList();
        string[] ids = [.. rows.Select(r => r.Text("Id") ?? "")];
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ids.Length; i++) byId.TryAdd(ids[i], i);
        var raw = rows.Select(r => r.Ints("__col1")).ToList();
        var shown = raw.Select(seen => seen.Where((_, i) => i % 2 == 0).ToList()).ToList();
        var used = new HashSet<int>(shown.SelectMany(k => k));
        used.UnionWith(roots);
        var children = new List<IReadOnlyList<int>>();
        for (int n = 0; n < rows.Count; n++)
        {
            var keys = shown[n];
            while (keys.Count > 0 && keys.Count < raw[n].Length)
            {
                int next = NextKey(ids, byId, used, keys[^1]) ?? keys[^1];
                used.Add(next);
                keys.Add(next);
            }
            children.Add(keys);
        }
        return [.. rows.Select((r, n) => new Node(n, ids[n], children[n], Math.Max(1, r.Int("Size")), r.Int("Angle"),
            r.Ints("__col4"), r.Ints("__col5"), r.Ints("__col7"), r.Ints("__col8"), r.Ints("__col9")))];
    }

    static int? NextKey(string[] ids, Dictionary<string, int> byId, HashSet<int> used, int last)
    {
        if (last < 0 || last >= ids.Length) return null;
        string id = ids[last];
        int digits = id.Length - id.Reverse().TakeWhile(char.IsDigit).Count();
        var names = new List<string>();
        if (digits < id.Length && int.TryParse(id[digits..], out int number)) names.Add(id[..digits] + (number + 1));
        names.Add(id + "_2");
        names.Add(id + "2");
        foreach (string name in names)
            if (byId.TryGetValue(name, out int row) && !used.Contains(row)) return row;
        return null;
    }

    /// <summary>One of the rows whose level range holds the area level, by weight; null when none fits.</summary>
    public static Topology? Pick(IReadOnlyList<Topology> topologies, int level, Random rng)
    {
        var fits = topologies.Where(t => t.Weight > 0 && t.MinLevel <= level && level <= t.MaxLevel).ToList();
        int total = fits.Sum(t => t.Weight);
        if (total <= 0) return null;
        int roll = rng.Next(total);
        foreach (var t in fits)
            if ((roll -= t.Weight) < 0) return t;
        return fits[^1];
    }

    static int At(IReadOnlyList<int> values, int branch) => values.Count == 0 ? 0 : values[Math.Min(branch, values.Count - 1)];

    /// <summary>Lays a topology out from the pump on walkable ground: each branch heads out at its angle for its rolled length, bends
    /// up to 90 degrees where the next cell is not walkable and ends short with no way on. A branch with a child goes on into it from
    /// its end; one without is a lane's far end. Then each branch's foundations stand along it, off to alternate sides.</summary>
    public static Plan Lay(IReadOnlyList<Node> nodes, Topology topology, (int X, int Y) pump, Func<int, int, bool> walkable, Random rng)
    {
        var lanes = new List<Lane>();
        var branches = new List<(List<(int X, int Y)> Cells, int Foundations)>();
        int copies = Math.Max(1, topology.Copies);
        double heading = rng.NextDouble() * 360;
        for (int copy = 0; copy < copies; copy++)
            Expand(nodes[topology.Root], pump, heading, 0, [pump], copy, copies, 0);

        void Expand(Node node, (int X, int Y) from, double toward, double delay, List<(int X, int Y)> path, int copy, int copiesHere, int depth)
        {
            if (depth > MaxDepth) return;
            int size = node.Size;
            if (node.Angle < 360 && copiesHere > 1) toward += 360.0 * copy / copiesHere;
            for (int b = 0; b < size; b++)
            {
                double angle = node.Angle >= 360 ? toward + 360.0 * (copy * size + b) / (copiesHere * size)
                    : size == 1 ? toward : toward + node.Angle * (b / (size - 1.0) - 0.5);
                int min = At(node.MinLength, b), max = Math.Max(min, At(node.MaxLength, b));
                double length = min + rng.NextDouble() * (max - min);
                int dmin = At(node.MinDelay, b), dmax = Math.Max(dmin, At(node.MaxDelay, b));
                double wait = Math.Max(delay, (dmin + rng.NextDouble() * (dmax - dmin)) / 1000);
                var (cells, end) = Trace(from, angle, length, walkable);
                branches.Add((cells, At(node.Foundations, b)));
                List<(int X, int Y)> lane = [.. path, .. cells];
                if (b < node.Children.Count && node.Children[b] >= 0 && node.Children[b] < nodes.Count && cells.Count > 0)
                    Expand(nodes[node.Children[b]], cells[^1], end, wait, lane, 0, 1, depth + 1);
                else if (cells.Count > 0) lanes.Add(new Lane(lane, wait));
            }
        }

        var lanePoints = lanes.SelectMany(l => l.Path).ToHashSet();
        var placed = new List<(int X, int Y)>();
        foreach (var (cells, count) in branches)
            for (int k = 0; k < count && cells.Count > 0; k++)
            {
                int i = Math.Clamp((int)Math.Round((k + 1.0) / (count + 1) * cells.Count) - 1, 0, cells.Count - 1);
                var p = cells[i];
                var q = cells[Math.Min(i + 1, cells.Count - 1)];
                var o = i > 0 ? cells[i - 1] : p;
                double dx = q.X - o.X, dy = q.Y - o.Y, len = Math.Max(1e-9, Math.Sqrt(dx * dx + dy * dy));
                int side = k % 2 == 0 ? 1 : -1;
                var want = ((int)Math.Round(p.X - dy / len * FoundationOffset * side), (int)Math.Round(p.Y + dx / len * FoundationOffset * side));
                if (Free(want, walkable, pump, placed, lanePoints) is { } cell) placed.Add(cell);
            }
        return new Plan(topology.Id, lanes, placed);
    }

    static (List<(int X, int Y)> Cells, double Heading) Trace((int X, int Y) from, double angle, double length, Func<int, int, bool> walkable)
    {
        var cells = new List<(int X, int Y)>();
        double x = from.X, y = from.Y, h = angle;
        for (double walked = 0; walked < length; walked += Step)
        {
            (int X, int Y)? next = null;
            foreach (int turn in new[] { 0, 15, -15, 30, -30, 45, -45, 60, -60, 75, -75, 90, -90 })
            {
                double r = (h + turn) * Math.PI / 180;
                var c = ((int)Math.Round(x + Step * Math.Cos(r)), (int)Math.Round(y + Step * Math.Sin(r)));
                if (!walkable(c.Item1, c.Item2)) continue;
                h += turn;
                (x, y) = (x + Step * Math.Cos(r), y + Step * Math.Sin(r));
                next = c;
                break;
            }
            if (next is not { } cell) break;
            if (cells.Count == 0 || cells[^1] != cell) cells.Add(cell);
        }
        return (cells, h);
    }

    static double D((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>The walkable cell nearest <paramref name="want"/> (rings out to 12 cells) that keeps its gaps from the pump, the placed
    /// foundations and the lanes' cells; null when there is none.</summary>
    public static (int X, int Y)? Free((int X, int Y) want, Func<int, int, bool> walkable, (int X, int Y) pump,
        IReadOnlyList<(int X, int Y)> placed, IReadOnlySet<(int X, int Y)>? lane = null)
    {
        bool OffLane((int X, int Y) c)
        {
            if (lane is null) return true;
            for (int dy = -LaneClearance + 1; dy < LaneClearance; dy++)
                for (int dx = -LaneClearance + 1; dx < LaneClearance; dx++)
                    if (lane.Contains((c.X + dx, c.Y + dy))) return false;
            return true;
        }
        bool Fits((int X, int Y) c) => walkable(c.X, c.Y) && D(c, pump) >= PumpGap && placed.All(p => D(c, p) >= FoundationGap) && OffLane(c);
        for (int r = 0; r <= 12; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var c = (want.X + dx, want.Y + dy);
                    if (Fits(c)) return c;
                }
        return null;
    }
}
