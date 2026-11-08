namespace GameServer.Core.World;

/// <summary>
/// The client's pathfinder (0x141e50f90), ported 1:1 so the server's copy of a client-moved walker takes the client's own
/// path (Agents/docs/protocol/07-latency-visibility-movement.md §5). A* over the walk grid in a 272 x 272 window around
/// the start: 8 neighbours (no corner rule), cost 1 / 1.4142, float32 throughout, an MSVC binary heap on f; a cell is
/// walkable for a mover of size s (Positioned +0x1e3 = object_size − 1) when its walk value is ≥ s + 1, and free of
/// blocking objects (Chebyshev distance − the object's size &lt; s + 1). The player has no expansion limit; cells outside
/// the ellipse |n − start| + |n − target| &lt; clamp(2 |target − start|, 20, 136) are not searched. Other movers stop after
/// a number of expansions (<see cref="SearchLimit"/>), and when that fails with the straight line blocked, search again
/// guided by a coarse path (A* over 11.5-cell cells). An unreachable target ends at the explored cell with the lowest
/// heuristic. The waypoints are the path's corners: walking back from the end, a cell is kept where the line (Bresenham)
/// from its parent to the last kept waypoint is blocked. A cell in range is the goal only with the line to the target on
/// the projectile grid at the order's precision (<see cref="LineBelow"/>): a bow's attack walks to where it can shoot.
/// </summary>
public static class ClientPathfinder
{
    /// <summary>A blocking object: its cell and size (Positioned +0x1e3 = object_size − 1).</summary>
    public readonly record struct Blocker(int X, int Y, int Size)
    {
        public double? CollisionRadius { get; init; }
    }

    /// <summary>How long a search may run (0x141e50f90, from the mover's components).</summary>
    public enum SearchLimit
    {
        /// <summary>A mover with a Player component: no expansion count, the ellipse instead.</summary>
        Player,
        /// <summary>A unique monster (ObjectMagicProperties rarity 3) or a pet (PetAi): 1000 expansions, coarse retry.</summary>
        Expansions1000,
        /// <summary>Any other mover: 100 expansions, coarse retry.</summary>
        Expansions100,
    }

    const int Window = 0x110, Half = 0x88, MaxWaypoints = 0x88, Tile = 23, CoarseLimit = 300;
    // DAT_142fc93e0 (dx, dy per direction) and DAT_142fc9420 (the opposite direction).
    static readonly (int X, int Y)[] Dirs = [(0, 1), (1, 0), (0, -1), (-1, 0), (1, 1), (1, -1), (-1, -1), (-1, 1)];
    static readonly byte[] Reverse = [2, 3, 0, 1, 6, 7, 4, 5];
    const byte NoParent = 8, Closed = 1, InObject = 2;

    struct Node { public int Gen; public byte Flags, Dir, Coarse; public float G, H, F; }

    [ThreadStatic] static Node[]? t_nodes;
    [ThreadStatic] static int t_gen;
    [ThreadStatic] static List<(int Index, float F)>? t_heap;

    /// <summary>
    /// The client's path from <paramref name="start"/> to <paramref name="target"/>: the waypoints in walking order
    /// (start first, then each corner, the end last), or empty when the walker stays (the target is its own cell, out of
    /// the map, or nothing better is reachable).
    /// </summary>
    /// <param name="walkable">Whether a cell's walk value is ≥ mover size + 1 (out of the map: false).</param>
    /// <param name="mapW">Map width in cells (the client refuses targets outside the map).</param>
    /// <param name="minRange">Stop at least this far from the target (cells; the walk's min range).</param>
    /// <param name="maxRange">Stop within this distance of the target (cells; 0 = on it).</param>
    /// <param name="tolerance">Subtracted from the distance to the target before the range test (the target object's
    /// size on a walk-up); −1 for none.</param>
    public static List<(int X, int Y)> FindPath((int X, int Y) start, (int X, int Y) target, Func<int, int, bool> walkable,
        int mapW, int mapH, IReadOnlyList<Blocker>? blockers = null, int moverSize = 1, int minRange = 0, int maxRange = 0,
        int tolerance = -1, SearchLimit limit = SearchLimit.Player) =>
        FindPath(start, target, walkable, mapW, mapH, blockers, moverSize, minRange, maxRange, tolerance, limit, out _);

    public static List<(int X, int Y)> FindPath((int X, int Y) start, (int X, int Y) target, Func<int, int, bool> walkable,
        int mapW, int mapH, IReadOnlyList<Blocker>? blockers, int moverSize, int minRange, int maxRange, int tolerance,
        SearchLimit limit, out bool reached) =>
        FindPath(start, target, walkable, mapW, mapH, blockers, moverSize, minRange, maxRange, tolerance, limit, null, 1, out reached);

    /// <param name="sight">The projectile grid (the client's walk layer 1: 0 … 5, 0 outside the map), when the goal must
    /// have the line to the target: a cell in range is the goal only if no cell of the line from it to the target, both
    /// ends included, is below <paramref name="precision"/> (0x141d8a7f0 at the order's precision: 1 for a Move or a
    /// melee attack, 3 for a bow's). Null: any cell in range.</param>
    /// <param name="precision">The order's precision (MoveTo +0x528, 1 … 5): the line test's threshold.</param>
    public static List<(int X, int Y)> FindPath((int X, int Y) start, (int X, int Y) target, Func<int, int, bool> walkable,
        int mapW, int mapH, IReadOnlyList<Blocker>? blockers, int moverSize, int minRange, int maxRange, int tolerance,
        SearchLimit limit, Func<int, int, int>? sight, int precision, out bool reached)
    {
        reached = false;
        if (target.X < 0 || target.Y < 0 || target.X >= mapW || target.Y >= mapH) return [];
        var ctx = new Search(start, target, walkable, blockers ?? [], moverSize, minRange, maxRange, tolerance, limit, sight, precision);
        // A monster whose straight line is blocked searches again, guided by a coarse path, when the first search fails.
        bool retry = limit != SearchLimit.Player && LineBlocked(start, target, ctx.Walkable);
        var path = ctx.Run(coarse: false, out reached);
        if (!reached && retry)
        {
            // When a cell in range lacked the line and the best cell was the start, the second search goes to the target itself.
            if (ctx.SightFailed && ctx.Best == start) ctx.ToTarget();
            path = ctx.Run(coarse: true, out reached);
        }
        return path;
    }

    /// <summary>
    /// Where an order's goal is met without a step (0x141e81950, what MoveTo tries before a search and an action tests
    /// before it swings): <paramref name="here"/> is within [<paramref name="minRange"/>, <paramref name="maxRange"/>] of
    /// the target, the target's size (<paramref name="tolerance"/>, −1 for none) widening the range, and, for a target
    /// with a size, the line to it on the projectile grid holds <paramref name="precision"/> up to the target's body:
    /// the last cell of the line at or above the threshold is within tolerance + 1 of the target (0x141d8ab90; a cell
    /// each side on the diagonal). Without a size the range alone decides.
    /// </summary>
    public static bool InReach((int X, int Y) here, (int X, int Y) target, int minRange, int maxRange, int tolerance,
        Func<int, int, int>? sight, int precision)
    {
        long d2 = (long)(here.X - target.X) * (here.X - target.X) + (long)(here.Y - target.Y) * (here.Y - target.Y);
        if (tolerance < 0) return d2 <= (long)maxRange * maxRange && (long)minRange * minRange <= d2;
        long max = maxRange + tolerance, min = minRange == 0 ? 0 : minRange - tolerance;
        if (d2 > max * max || d2 < min * min) return false;
        if (here == target || maxRange <= tolerance || sight is null) return true;
        if (!LineBelow(here, target, sight, precision, out var last)) return true;
        int dx = Math.Abs(target.X - last.X), dy = Math.Abs(target.Y - last.Y);
        int left = tolerance < dx
            ? tolerance < dy ? dx + dy - 2 * tolerance - 2 : Math.Max(dx - tolerance - 1, 0)
            : Math.Max(dy - tolerance - 1, 0);
        return left == 0;
    }

    sealed class Search((int X, int Y) start, (int X, int Y) target, Func<int, int, bool> walkable,
        IReadOnlyList<Blocker> blockers, int moverSize, int minRange, int maxRange, int tolerance, SearchLimit limit,
        Func<int, int, int>? sight, int precision)
    {
        public Func<int, int, bool> Walkable => walkable;
        /// <summary>A cell in range was passed over for want of the line to the target.</summary>
        public bool SightFailed { get; private set; }
        /// <summary>The explored cell with the lowest heuristic, the first of its value.</summary>
        public (int X, int Y) Best { get; private set; }
        public void ToTarget() => (minRange, maxRange) = (0, 0);
        readonly int _threshold = moverSize + 1, _objectRadius = 5 - 1 + moverSize;   // DAT_143a6ae58 = 5
        readonly int _vx = target.X - start.X, _vy = target.Y - start.Y;
        // The coarse waypoints (goal first) and the path length from each to the target (DAT_1445c69c0 / 0x1445c69d8).
        readonly List<(int X, int Y)> _coarse = [];
        readonly List<float> _coarseLength = [];

        bool InObjectAt(int x, int y) => ObjectBlocks(blockers, x, y, _threshold, _objectRadius);

        // 0x141e50ed0: the distance to the target (less the tolerance) outside [min, max], plus 0.001 × |cross|.
        float Heuristic(int x, int y)
        {
            int wx = x - target.X, wy = y - target.Y;
            float d = MathF.Sqrt((uint)(wx * wx + wy * wy));
            if (tolerance >= 0) d = MathF.Max(d - tolerance, 0f);
            float cross = Math.Abs(_vy * wx - wy * _vx) * 0.001f;
            if (maxRange < d) return d - maxRange + cross;
            if (d < minRange) return minRange - d + cross;
            return 0f;
        }

        static float Distance((int X, int Y) a, (int X, int Y) b) =>
            MathF.Sqrt((uint)((a.Y - b.Y) * (a.Y - b.Y) + (a.X - b.X) * (a.X - b.X)));

        // 0x141e52590: the coarse waypoint a cell heads for (an index into _coarse + 1; 0 = the target itself).
        byte Progress(byte index, (int X, int Y) cell)
        {
            static uint D2((int X, int Y) a, (int X, int Y) b) => (uint)((a.Y - b.Y) * (a.Y - b.Y) + (a.X - b.X) * (a.X - b.X));
            if (index != 0 && D2(_coarse[index - 1], cell) <= 0x8f && !LineBlocked(_coarse[index - 1], cell, walkable))
                return (byte)(index - 1);
            if (index > 1 && D2(_coarse[index - 2], cell) < 0x90 && !LineBlocked(_coarse[index - 2], cell, walkable))
                return (byte)(index - 2);
            return index;
        }

        float GuidedHeuristic(int x, int y, byte index)
        {
            float h = Heuristic(x, y);
            if (0f < h && index != 0) h = Distance((x, y), _coarse[index - 1]) + _coarseLength[index - 1];
            return h;
        }

        public List<(int X, int Y)> Run(bool coarse, out bool reached)
        {
            reached = false;
            var nodes = t_nodes ??= new Node[Window * Window];
            var heap = t_heap ??= [];
            _coarse.Clear();
            _coarseLength.Clear();
            if (coarse) CoarsePass(nodes, heap);

            heap.Clear();
            int gen = ++t_gen;
            float ellipse = MathF.Max(MathF.Min(MathF.Sqrt(_vx * _vx + _vy * _vy) * 2, 136f), 20f);
            int maxExpansions = limit switch { SearchLimit.Expansions1000 => 1000, SearchLimit.Expansions100 => 100, _ => -1 };

            int Index(int x, int y) => (x - start.X + Half) * Window + (y - start.Y + Half);
            void Close(ref Node n)
            {
                if (n.Gen != gen) { n.Gen = gen; n.Flags = 0; }
                n.Flags |= Closed;
            }

            int si = Index(start.X, start.Y);
            ref var s = ref nodes[si];
            s.Gen = gen;
            s.Flags = (byte)(!walkable(start.X, start.Y) || InObjectAt(start.X, start.Y) ? InObject : 0);
            s.Dir = NoParent;
            s.Coarse = Progress(_coarse.Count == 0 ? (byte)0 : (byte)(_coarse.Count - 1), start);
            s.G = 0;
            s.H = GuidedHeuristic(start.X, start.Y, s.Coarse);
            s.F = s.H + 0f;
            Push(heap, si, s.F);

            (int X, int Y) best = (0, 0), end = (0, 0);
            float bestH = float.MaxValue;
            int expansions = 0;
            while (heap.Count > 0)
            {
                var (ci, cf) = Pop(heap);
                ref var c = ref nodes[ci];
                if (cf != c.F) continue;   // a stale entry: the node was reached cheaper since
                int cx = start.X - Half + ci / Window, cy = start.Y - Half + ci % Window;
                if (c.H < bestH) { best = (cx, cy); bestH = c.H; }
                if (c.H == 0f)
                {
                    // The goal line test (0x141d8a7f0 on the projectile grid at the order's precision): a cell in range
                    // without the line to the target is searched on from, like any other.
                    if (sight is null || !LineBelow((cx, cy), target, sight, precision)) { end = (cx, cy); reached = true; break; }
                    SightFailed = true;
                }
                if (maxExpansions >= 0 && ++expansions > maxExpansions) break;
                Close(ref c);
                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + Dirs[d].X, ny = cy + Dirs[d].Y;
                    uint wx = (uint)(nx - start.X + Half), wy = (uint)(ny - start.Y + Half);
                    if (wx >= Window || wy >= Window) continue;
                    int ni = (int)wx * Window + (int)wy;
                    ref var n = ref nodes[ni];
                    if (n.Gen == gen && (n.Flags & Closed) != 0) continue;
                    if (limit == SearchLimit.Player)
                    {
                        int tx = nx - target.X, ty = ny - target.Y, ox = nx - start.X, oy = ny - start.Y;
                        if (!(ellipse > MathF.Sqrt(tx * tx + ty * ty) + MathF.Sqrt(ox * ox + oy * oy))) continue;
                    }
                    if (!walkable(nx, ny)) { Close(ref n); continue; }
                    bool penalty = false;
                    if (InObjectAt(nx, ny))
                    {
                        // Only a walker already inside an object's footprint may cross others (cost 21 a cell).
                        if ((c.Flags & InObject) == 0) { Close(ref n); continue; }
                        penalty = true;
                    }
                    float g = c.G + (penalty ? 21f : d < 4 ? 1f : 1.4142f);
                    if (n.Gen == gen && !(g < n.G)) continue;
                    byte progress = Progress(c.Coarse, (nx, ny));
                    float h = GuidedHeuristic(nx, ny, progress);
                    if (n.Gen != gen) { n.Gen = gen; n.Flags = 0; }
                    n.Coarse = progress;
                    n.G = g;
                    n.H = h;
                    n.F = h + g;
                    n.Dir = Reverse[d];
                    if (penalty) n.Flags |= InObject;
                    Push(heap, ni, n.F);
                }
            }
            Best = best;
            if (!reached) end = best;
            if (end == start) return [];

            // Corners, from the end back to the start.
            var corners = new List<(int X, int Y)> { end };
            var anchor = end;
            var p = Step(end, nodes[Index(end.X, end.Y)].Dir);
            while (p != start)
            {
                var q = Step(p, nodes[Index(p.X, p.Y)].Dir);
                if (LineBlocked(q, anchor, walkable) || LineInObject(q, anchor, InObjectAt))
                {
                    if (corners.Count == MaxWaypoints) return [];
                    anchor = p;
                    corners.Add(p);
                }
                p = q;
            }
            if (corners.Count == MaxWaypoints) return [];
            corners.Add(start);
            corners.Reverse();
            return corners;
        }

        // The coarse pass: A* over cells of 11.5 grid units ((c × 23) / 2) from the start's coarse cell and three of its
        // neighbours, through cells within 136 of the target that a straight line reaches, at most 300 expansions; a coarse
        // cell with h < 23 that sees the target ends it, and its chain back to a seed becomes the guiding waypoints.
        void CoarsePass(Node[] nodes, List<(int Index, float F)> heap)
        {
            heap.Clear();
            int gen = ++t_gen;
            (int X, int Y) c0 = (start.X * 2 / Tile, start.Y * 2 / Tile);
            static (int X, int Y) Pos((int X, int Y) c) => (c.X * Tile / 2, c.Y * Tile / 2);
            int Index((int X, int Y) c) => (c.X - c0.X + Half) * Window + (c.Y - c0.Y + Half);

            foreach (int d in new[] { -1, 0, 1, 4 })
            {
                var c = d < 0 ? c0 : (c0.X + Dirs[d].X, c0.Y + Dirs[d].Y);
                var p = Pos(c);
                if (LineBlocked(start, p, walkable)) continue;
                ref var n = ref nodes[Index(c)];
                if (n.Gen != gen) { n.Gen = gen; n.Flags = 0; }
                n.G = Distance(start, p);
                n.H = Heuristic(p.X, p.Y);
                n.F = n.H + n.G;
                n.Dir = NoParent;
                Push(heap, Index(c), n.F);
            }
            int expansions = 0;
            while (heap.Count > 0)
            {
                var (ci, cf) = Pop(heap);
                ref var node = ref nodes[ci];
                if (cf != node.F) continue;
                (int X, int Y) c = (c0.X - Half + ci / Window, c0.Y - Half + ci % Window);
                var p = Pos(c);
                if (node.H < 23f && !LineBlocked(p, target, walkable))
                {
                    // The guiding waypoints, goal first, with the length from each to the target.
                    float length = 0f;
                    var prev = target;
                    int i = ci;
                    while (true)
                    {
                        (int X, int Y) cc = (c0.X - Half + i / Window, c0.Y - Half + i % Window);
                        var pos = Pos(cc);
                        length += Distance(pos, prev);
                        prev = pos;
                        _coarse.Add(pos);
                        _coarseLength.Add(length);
                        byte dir = nodes[i].Dir;
                        if (dir == NoParent) break;
                        int ix = i / Window + Dirs[dir].X, iy = i % Window + Dirs[dir].Y;
                        if (ix < 0 || ix >= Window || iy < 0 || iy >= Window) break;
                        i = ix * Window + iy;
                    }
                    return;
                }
                if (++expansions > CoarseLimit) return;
                if (node.Gen != gen) { node.Gen = gen; node.Flags = 0; }
                node.Flags |= Closed;
                for (int d = 0; d < 8; d++)
                {
                    (int X, int Y) nc = (c.X + Dirs[d].X, c.Y + Dirs[d].Y);
                    uint wx = (uint)(nc.X - c0.X + Half), wy = (uint)(nc.Y - c0.Y + Half);
                    if (wx >= Window || wy >= Window) continue;
                    int ni = (int)wx * Window + (int)wy;
                    ref var n = ref nodes[ni];
                    if (n.Gen == gen && (n.Flags & Closed) != 0) continue;
                    var np = Pos(nc);
                    if (Math.Abs(np.X - target.X) >= Half || Math.Abs(np.Y - target.Y) >= Half) continue;
                    if (LineBlocked(p, np, walkable)) continue;
                    float g = node.G + (d < 4 ? 11.5f : 16.2634f);
                    if (n.Gen == gen && !(g < n.G)) continue;
                    float h = Heuristic(np.X, np.Y);
                    if (n.Gen != gen) { n.Gen = gen; n.Flags = 0; }
                    n.G = g;
                    n.H = h;
                    n.F = h + g;
                    n.Dir = Reverse[d];
                    Push(heap, ni, n.F);
                }
            }
        }
    }

    static (int X, int Y) Step((int X, int Y) c, byte dir) => (c.X + Dirs[dir].X, c.Y + Dirs[dir].Y);

    /// <summary>Whether a blocking object covers a cell for a mover (0x141e65910 / 0x141e65ae0): objects are found in the
    /// 23-cell tiles within <paramref name="radius"/> of the cell, and block when their Chebyshev distance − their size is
    /// below <paramref name="threshold"/> (mover size + 1).</summary>
    public static bool ObjectBlocks(IReadOnlyList<Blocker> blockers, int x, int y, int threshold, int radius)
    {
        if (blockers.Count == 0) return false;
        if (blockers is IBlockerQuery indexed) return indexed.Blocks(x, y, threshold, radius);
        int tx0 = Math.Max(0, (x - radius) / Tile), tx1 = (x + radius) / Tile + 1;
        int ty0 = Math.Max(0, (y - radius) / Tile), ty1 = (y + radius) / Tile + 1;
        foreach (var b in blockers)
            if (BlockerBlocks(b, x, y, threshold, radius, tx0, tx1, ty0, ty1)) return true;
        return false;
    }

    internal static bool BlockerBlocks(Blocker b, int x, int y, int threshold, int radius, int tx0, int tx1, int ty0, int ty1)
    {
        if (b.CollisionRadius is double hull)
        {
            double dx = b.X - x, dy = b.Y - y, clearance = hull + threshold;
            if (dx * dx + dy * dy < clearance * clearance) return true;
        }
        int btx = b.X / Tile, bty = b.Y / Tile;
        // Large bodies can extend across several tile buckets; their centre may be outside this query.
        if ((btx < tx0 || btx >= tx1 || bty < ty0 || bty >= ty1) && b.Size + threshold <= radius) return false;
        return Math.Max(Math.Abs(b.X - x), Math.Abs(b.Y - y)) - b.Size < threshold;
    }

    /// <summary>The cells of the client's line from <paramref name="a"/> to <paramref name="b"/>, both ends included
    /// (the Bresenham of 0x141d8a7f0, and of a walk segment, 0x141e50880 / 0x141e50a90).</summary>
    public static IEnumerable<(int X, int Y)> Line((int X, int Y) a, (int X, int Y) b)
    {
        bool steep = Math.Abs(b.X - a.X) < Math.Abs(b.Y - a.Y);
        int major = steep ? a.Y : a.X, majorEnd = steep ? b.Y : b.X, minor = steep ? a.X : a.Y, minorEnd = steep ? b.X : b.Y;
        int majorLen = Math.Abs(majorEnd - major), minorLen = Math.Abs(minorEnd - minor);
        int majorStep = major < majorEnd ? 1 : -1, minorStep = minor < minorEnd ? 1 : minor == minorEnd ? 0 : -1;
        int err = -(majorLen / 2);
        while (major != majorEnd + majorStep)
        {
            yield return steep ? (minor, major) : (major, minor);
            int e = minorLen + err;
            major += majorStep;
            if (e < 1) err = e;
            else { err = e - majorLen; minor += minorStep; }
        }
    }

    static bool LineBlocked((int X, int Y) a, (int X, int Y) b, Func<int, int, bool> free)
    {
        foreach (var (x, y) in Line(a, b))
            if (!free(x, y)) return true;
        return false;
    }

    /// <summary>The client's line test against a threshold (0x141d8a7f0): whether a cell of the line from
    /// <paramref name="a"/> to <paramref name="b"/>, both ends included, has a value below <paramref name="threshold"/>
    /// (a negative threshold: never).</summary>
    public static bool LineBelow((int X, int Y) a, (int X, int Y) b, Func<int, int, int> value, int threshold) =>
        LineBelow(a, b, value, threshold, out _);

    /// <summary>The same test reporting where the line got to (0x141d8ab90): the last cell at or above the threshold
    /// before the one below it, <paramref name="a"/> itself when that is the first.</summary>
    public static bool LineBelow((int X, int Y) a, (int X, int Y) b, Func<int, int, int> value, int threshold, out (int X, int Y) last)
    {
        last = a;
        if (threshold < 0) return false;
        foreach (var cell in Line(a, b))
        {
            if (value(cell.X, cell.Y) < threshold) return true;
            last = cell;
        }
        return false;
    }

    // The object line check (0x141e65c40): blocked when any cell of the line is inside an object.
    static bool LineInObject((int X, int Y) a, (int X, int Y) b, Func<int, int, bool> inObject)
    {
        foreach (var (x, y) in Line(a, b))
            if (inObject(x, y)) return true;
        return false;
    }

    // MSVC std::push_heap / std::pop_heap with the comparator a.F > b.F (0x141bedeb0 / 0x141bedd40 / 0x141bedd30).
    static bool Less((int, float F) a, (int, float F) b) => a.F > b.F;

    static void Push(List<(int Index, float F)> heap, int index, float f)
    {
        heap.Add((index, f));
        SiftUp(heap, heap.Count - 1, 0, (index, f));
    }

    static void SiftUp(List<(int Index, float F)> heap, int hole, int top, (int Index, float F) val)
    {
        while (top < hole)
        {
            int idx = (hole - 1) >> 1;
            if (!Less(heap[idx], val)) break;
            heap[hole] = heap[idx];
            hole = idx;
        }
        heap[hole] = val;
    }

    static (int Index, float F) Pop(List<(int Index, float F)> heap)
    {
        int last = heap.Count - 1;
        var first = heap[0];
        if (last >= 1)
        {
            var val = heap[last];
            heap[last] = first;
            int bottom = last, hole = 0, idx = 0, maxNonLeaf = (bottom - 1) >> 1;
            while (idx < maxNonLeaf)
            {
                idx = 2 * idx + 2;
                if (Less(heap[idx], heap[idx - 1])) idx--;
                heap[hole] = heap[idx];
                hole = idx;
            }
            if (idx == maxNonLeaf && bottom % 2 == 0)
            {
                heap[hole] = heap[bottom - 1];
                hole = bottom - 1;
            }
            SiftUp(heap, hole, 0, val);
        }
        heap.RemoveAt(last);
        return first;
    }
}
