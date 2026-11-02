namespace GameServer.Core.World;

/// <summary>
/// Server-side estimate of where a client-predicted walker is. The client walks on its own after a 0x1ab echo, but the
/// echo carries the server's idea of the actor's position, and the client snaps to it when it is ~92+ units off
/// (Actor handler 0x1421940a0). So the server follows each order along an A* path over the walkability grid (8
/// neighbours) at the client's walking speed, ending at the closest reachable cell when the target itself is blocked,
/// and reports that position with the next order.
/// </summary>
public sealed class WalkEstimator
{
    private readonly int _w, _h;
    private readonly Func<int, int, bool> _walkable;
    private double _speed;
    private List<(int X, int Y)> _path;
    private double[] _cumulative = [0];
    private double _t0;
    private readonly Func<IReadOnlyList<ClientPathfinder.Blocker>>? _blockers;
    private readonly int _size;
    private readonly ClientPathfinder.SearchLimit _limit;
    private (int X, int Y) _target;
    private bool _reached = true;

    /// <param name="walkable">Whether a grid cell is walkable (all cells when null).</param>
    /// <param name="speed">Client walking speed in grid units per second (~37 measured live).</param>
    public WalkEstimator(int x, int y, double speed, Func<int, int, bool>? walkable = null, int width = 4096, int height = 4096,
        Func<IReadOnlyList<ClientPathfinder.Blocker>>? blockers = null, int size = 1,
        ClientPathfinder.SearchLimit limit = ClientPathfinder.SearchLimit.Expansions100)
    {
        _walkable = walkable ?? ((_, _) => true);
        _speed = speed;
        (_w, _h) = (width, height);
        (_blockers, _size, _limit) = (blockers, size, limit);
        _path = [(x, y)];
        _target = (x, y);
    }

    public double Speed => _speed;

    /// <summary>The cell the current order walks to.</summary>
    public (int X, int Y) Target => _target;

    /// <summary>Whether the current path ends on <see cref="Target"/>. A path the search cut short (the client's expansion
    /// limit) is walked to its end, and then the client paths again from there (0x141e7bec0 → 0x141e7b920).</summary>
    public bool Reached => _reached;

    /// <summary>The path began on a blocked cell (0x141e4d890's out flag → Positioned +0x1e5 &amp; 4): the walker steps
    /// through bodies until its first free step; terrain still stops it (0x141e32710).</summary>
    public bool WalksThrough { get; set; }

    /// <summary>The steps of the current walk after <paramref name="from"/> up to <paramref name="to"/>: when each cell is
    /// entered, in order (the Bresenham cells of each segment, as <see cref="Position"/> reports them).</summary>
    public List<(double At, (int X, int Y) Cell)> Steps(double from, double to)
    {
        var steps = new List<(double, (int, int))>();
        for (int i = 1; i < _path.Count; i++)
        {
            double start = _t0 + _cumulative[i - 1] / _speed, end = _t0 + _cumulative[i] / _speed;
            if (end <= from) continue;
            if (start > to) break;
            var line = ClientPathfinder.Line(_path[i - 1], _path[i]).ToList();
            int n = line.Count - 1;
            for (int k = 1; k <= n; k++)
            {
                double at = start + (end - start) * k / n;
                if (at > to) return steps;
                if (at > from) steps.Add((at, line[k]));
            }
        }
        return steps;
    }

    /// <summary>Changes the walking speed from <paramref name="now"/> on (e.g. a movement speed stat changed); the walk in
    /// progress continues from where it is.</summary>
    public void SetSpeed(double speed, double now)
    {
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        double d = Math.Max(0, _speed * (now - _t0));
        _speed = speed;
        _t0 = now - d / speed;
    }

    /// <summary>Estimated position at <paramref name="now"/> (seconds, any monotonic clock).</summary>
    public (int X, int Y) Position(double now) => At(now, out _);

    // The client's cell: a Bresenham step at a time along the segment (see PlayerWalk), and how far into the next step.
    private (int X, int Y) At(double now, out double fraction)
    {
        fraction = 0;
        double d = Math.Max(0, _speed * (now - _t0));
        int i = Array.BinarySearch(_cumulative, d);
        if (i >= 0) return _path[i];
        i = ~i;
        if (i >= _path.Count) return _path[^1];
        var (a, b) = (_path[i - 1], _path[i]);
        int total = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        double steps = (d - _cumulative[i - 1]) / (_cumulative[i] - _cumulative[i - 1]) * total;
        int done = Math.Min((int)steps, total);
        fraction = steps - done;
        return ClientPathfinder.Line(a, b).ElementAt(done);
    }

    /// <summary>A new walk order at <paramref name="now"/>. Returns where the actor is now (what the echo must report).
    /// As in the client, the step in progress keeps its elapsed fraction: chase orders come every ~125 ms, under one step
    /// at low speeds, and rounding to a cell on each gained up to √2 of the speed on diagonals (13 cells off, live
    /// 2026-11-01), which the client never corrects for a monster.</summary>
    public (int X, int Y) Order(int targetX, int targetY, double now)
    {
        var here = At(now, out double fraction);
        _target = (targetX, targetY);
        if (_blockers is null)
        {
            _path = FindPath(here, _target);
            _reached = _path.Count > 0 && _path[^1] == _target;
            WalksThrough = false;
        }
        else
        {
            var bodies = _blockers();
            _path = ClientPathfinder.FindPath(here, _target, _walkable, _w, _h, bodies, _size, 0, 0, -1, _limit, out _reached);
            WalksThrough = !_walkable(here.X, here.Y) || ClientPathfinder.ObjectBlocks(bodies, here.X, here.Y, _size + 1, _size + 4);
        }
        // No step to take: the client ends the action instead of pathing again (0x141e7b920 with nothing found).
        if (_path.Count < 2) _reached = true;
        if (_path.Count == 0) _path = [here];
        _cumulative = new double[_path.Count];
        for (int i = 1; i < _path.Count; i++)
            _cumulative[i] = _cumulative[i - 1] + Math.Sqrt(Math.Pow(_path[i].X - _path[i - 1].X, 2) + Math.Pow(_path[i].Y - _path[i - 1].Y, 2));
        _t0 = now;
        if (_path.Count > 1)
            _t0 -= fraction * _cumulative[1] / Math.Max(Math.Abs(_path[1].X - here.X), Math.Abs(_path[1].Y - here.Y)) / _speed;
        return here;
    }

    /// <summary>Puts the walker at a cell (a teleport, e.g. S→C 0x1b7): no walk in progress.</summary>
    public void Teleport(int x, int y, double now)
    {
        _path = [(x, y)];
        _cumulative = [0];
        _t0 = now;
        _target = (x, y);
        _reached = true;
        WalksThrough = false;
    }

    /// <summary>When the current walk ends.</summary>
    public double ArrivesAt => _t0 + _cumulative[^1] / _speed;

    /// <summary>
    /// Walks toward a target only until it is within <paramref name="reach"/> cells (a melee attack or a click on an
    /// object: the client walks into range first). Returns where the walker is now, where it stops and when; when it
    /// is already in reach it stays.
    /// </summary>
    public ((int X, int Y) Here, (int X, int Y) Stop, double At) Approach(int targetX, int targetY, double reach, double now)
    {
        var here = Order(targetX, targetY, now);
        static double Dist((int X, int Y) a, int x, int y) => Math.Sqrt((a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y));
        int stop = _path.FindIndex(p => Dist(p, targetX, targetY) <= reach);
        if (stop < 0) stop = _path.Count - 1;   // unreachable: stop where the path ends (the caller checks the reach)
        _path = _path[..(stop + 1)];
        _cumulative = _cumulative[..(stop + 1)];
        _reached = true;
        return (here, _path[^1], ArrivesAt);
    }

    private bool Free(int x, int y) => x >= 0 && y >= 0 && x < _w && y < _h && _walkable(x, y);

    // A* from start to goal over paths no longer than 1.2 x the straight distance + 5; if the goal is not reachable that
    // way, the walker stops at the explored cell closest to it. The client also gives up near the edge in the click's
    // direction rather than take a long detour, even to a reachable target. Fitted to four recorded client walks
    // (2026-10-16, Frida): with walkable = mask >= 2 every end point is within ~4.5 cells; with 1.5 x + 10 one was 79 off.
    private List<(int X, int Y)> FindPath((int X, int Y) start, (int X, int Y) goal, int maxExpanded = 200_000)
    {
        static double H((int X, int Y) a, (int X, int Y) b)
        {
            int dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
            return Math.Max(dx, dy) + (Math.Sqrt(2) - 1) * Math.Min(dx, dy);
        }
        var open = new PriorityQueue<(int X, int Y), double>();
        var g = new Dictionary<(int, int), double> { [start] = 0 };
        var parent = new Dictionary<(int, int), (int, int)>();
        var best = start;
        double bestH = H(start, goal), fallbackCap = 1.2 * H(start, goal) + 5;
        open.Enqueue(start, bestH);
        int expanded = 0;
        while (open.TryDequeue(out var cur, out _) && expanded++ < maxExpanded)
        {
            if (cur == goal) { best = cur; break; }
            double h = H(cur, goal);
            if (h < bestH) (best, bestH) = (cur, h);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var n = (cur.X + dx, cur.Y + dy);
                    if (!Free(n.Item1, n.Item2)) continue;
                    // no corner cutting through blocked cells
                    if (dx != 0 && dy != 0 && (!Free(cur.X + dx, cur.Y) || !Free(cur.X, cur.Y + dy))) continue;
                    double ng = g[cur] + (dx != 0 && dy != 0 ? Math.Sqrt(2) : 1);
                    if (ng > fallbackCap) continue;
                    if (g.TryGetValue(n, out double old) && old <= ng) continue;
                    g[n] = ng;
                    parent[n] = cur;
                    open.Enqueue(n, ng + H(n, goal));
                }
        }
        var path = new List<(int X, int Y)> { best };
        while (parent.TryGetValue(path[^1], out var p)) path.Add(p);
        path.Reverse();
        return path;
    }
}
