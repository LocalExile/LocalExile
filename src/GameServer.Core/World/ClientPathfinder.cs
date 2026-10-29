namespace GameServer.Core.World;

/// <summary>
/// The client's pathfinder (0x141e4d890), ported 1:1 so the server's copy of a client-moved walker takes the client's own
/// path (Agents/docs/protocol/07-latency-visibility-movement.md §5). A* over the walk grid in a 272 x 272 window around
/// the start: 8 neighbours (no corner rule), cost 1 / 1.4142, float32 throughout, an MSVC binary heap on f; a cell is
/// walkable for a mover of size s (Positioned +0x1e3 = object_size − 1) when its walk value is ≥ s + 1, and free of
/// blocking objects (Chebyshev distance − the object's size &lt; s + 1). The player has no expansion limit; cells outside
/// the ellipse |n − start| + |n − target| &lt; clamp(2 |target − start|, 20, 136) are not searched. Other movers stop after
/// a number of expansions (<see cref="SearchLimit"/>), and when that fails with the straight line blocked, search again
/// guided by a coarse path (A* over 11.5-cell cells). An unreachable target ends at the explored cell with the lowest
/// heuristic. The waypoints are the path's corners: walking back from the end, a cell is kept where the line (Bresenham)
/// from its parent to the last kept waypoint is blocked.
/// </summary>
public static class ClientPathfinder
{
    /// <summary>A blocking object: its cell and size (Positioned +0x1e3 = object_size − 1).</summary>
    public readonly record struct Blocker(int X, int Y, int Size)
    {
        public double? CollisionRadius { get; init; }
    }

    /// <summary>How long a search may run (0x141e4d890, from the mover's components).</summary>
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
    // DAT_142fbf350 (dx, dy per direction) and DAT_142fbf330 (the opposite direction).
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
        SearchLimit limit, out bool reached)
    {
        reached = false;
        if (target.X < 0 || target.Y < 0 || target.X >= mapW || target.Y >= mapH) return [];
        var ctx = new Search(start, target, walkable, blockers ?? [], moverSize, minRange, maxRange, tolerance, limit);
        // A monster whose straight line is blocked searches again, guided by a coarse path, when the first search fails.
        bool retry = limit != SearchLimit.Player && LineBlocked(start, target, ctx.Walkable);
        var path = ctx.Run(coarse: false, out reached);
        if (!reached && retry) path = ctx.Run(coarse: true, out reached);
        return path;
    }

    sealed class Search((int X, int Y) start, (int X, int Y) target, Func<int, int, bool> walkable,
        IReadOnlyList<Blocker> blockers, int moverSize, int minRange, int maxRange, int tolerance, SearchLimit limit)
    {
        public Func<int, int, bool> Walkable => walkable;
        readonly int _threshold = moverSize + 1, _objectRadius = 5 - 1 + moverSize;   // DAT_143a5fe58 = 5
        readonly int _vx = target.X - start.X, _vy = target.Y - start.Y;
        // The coarse waypoints (goal first) and the path length from each to the target (DAT_1445bb850 / 0x1445bb868).
        readonly List<(int X, int Y)> _coarse = [];
        readonly List<float> _coarseLength = [];

        bool InObjectAt(int x, int y) => ObjectBlocks(blockers, x, y, _threshold, _objectRadius);

        // 0x141e4d7d0: the distance to the target (less the tolerance) outside [min, max], plus 0.001 × |cross|.
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

        // 0x141e4ee90: the coarse waypoint a cell heads for (an index into _coarse + 1; 0 = the target itself).
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
                if (c.H == 0f) { end = (cx, cy); reached = true; break; }   // the goal line check (layer 1) never fails on walkable cells
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

    /// <summary>Whether a blocking object covers a cell for a mover (0x141e61370 / 0x141e61540): objects are found in the
    /// 23-cell tiles within <paramref name="radius"/> of the cell, and block when their Chebyshev distance − their size is
    /// below <paramref name="threshold"/> (mover size + 1).</summary>
    public static bool ObjectBlocks(IReadOnlyList<Blocker> blockers, int x, int y, int threshold, int radius)
    {
        if (blockers.Count == 0) return false;
        int tx0 = Math.Max(0, (x - radius) / Tile), tx1 = (x + radius) / Tile + 1;
        int ty0 = Math.Max(0, (y - radius) / Tile), ty1 = (y + radius) / Tile + 1;
        // A BlockerSet yields only the blockers that can reach this cell; the others could not block it anyway.
        foreach (var b in blockers is BlockerSet set ? set.Near(x, y, threshold) : blockers)
        {
            if (b.CollisionRadius is double hull)
            {
                double dx = b.X - x, dy = b.Y - y, clearance = hull + threshold;
                if (dx * dx + dy * dy < clearance * clearance) return true;
            }
            int btx = b.X / Tile, bty = b.Y / Tile;
            // Large bodies can extend across several tile buckets; their centre may be outside this query.
            if ((btx < tx0 || btx >= tx1 || bty < ty0 || bty >= ty1) && b.Size + threshold <= radius) continue;
            if (Math.Max(Math.Abs(b.X - x), Math.Abs(b.Y - y)) - b.Size < threshold) return true;
        }
        return false;
    }

    /// <summary>The cells of the client's line from <paramref name="a"/> to <paramref name="b"/>, both ends included
    /// (the Bresenham of 0x141d87160, and of a walk segment, 0x141e4d180 / 0x141e4d390).</summary>
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

    // The object line check (0x141e616a0): blocked when any cell of the line is inside an object.
    static bool LineInObject((int X, int Y) a, (int X, int Y) b, Func<int, int, bool> inObject)
    {
        foreach (var (x, y) in Line(a, b))
            if (inObject(x, y)) return true;
        return false;
    }

    // MSVC std::push_heap / std::pop_heap with the comparator a.F > b.F (0x141beb420 / 0x141beb2b0 / 0x141beb2a0).
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
