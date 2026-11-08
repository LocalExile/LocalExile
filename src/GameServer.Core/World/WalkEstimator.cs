namespace GameServer.Core.World;

/// <summary>
/// Server-side estimate of where a client-predicted walker is. The client walks on its own after a 0x1ab echo, but the
/// echo carries the server's idea of the actor's position, and the client snaps to it when it is ~92+ units off
/// (Actor handler 0x14219aa10). So the server follows each order along an A* path over the walkability grid (8
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
    }

    public double Speed => _speed;

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
    /// As in the client, the step in progress keeps its elapsed fraction: rounding to a cell on every order gained up to √2
    /// of the speed on diagonals.</summary>
    public (int X, int Y) Order(int targetX, int targetY, double now)
    {
        var here = At(now, out double fraction);
        _path = _blockers is null ? FindPath(here, (targetX, targetY))
            : ClientPathfinder.FindPath(here, (targetX, targetY), _walkable, _w, _h, _blockers(), _size, limit: _limit);
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
