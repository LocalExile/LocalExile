namespace GameServer.Core.World;

/// <summary>
/// The server's copy of the local player's client-predicted walk (Agents/docs/protocol/07-latency-visibility-movement.md
/// §5). Each order takes the client's own path (<see cref="ClientPathfinder"/>) from the player's cell, and the walker
/// moves as the client's Positioned does (0x141e34400 → 0x141e4d180, ticked by 0x141e32710 → 0x141e4d390): every
/// segment between two waypoints is a Bresenham line of cells, one step per (|segment| / speed) / (major-axis steps)
/// seconds, so the cell is always a whole grid cell. A new order mid-step keeps the step's elapsed fraction (the
/// client rescales its time accumulator to the new step time). The 0x1ab echo reports <see cref="Position"/>; the
/// client snaps to it only when it is ~92+ cells off (Actor handler 0x1421940a0).
/// </summary>
public sealed class PlayerWalk
{
    private readonly Func<int, int, bool> _walkable;
    private readonly Func<IReadOnlyList<ClientPathfinder.Blocker>> _blockers;
    private readonly Func<IReadOnlyList<ClientPathfinder.Blocker>>? _stationary;
    private readonly int _w, _h, _size;
    private double _speed;
    private List<Segment> _segments = [];
    private (int X, int Y) _rest;
    private double _restSince;
    private bool _escapingCrowd;

    private sealed record Segment((int X, int Y) From, (int X, int Y)[] Cells, double T0, float Step)
    {
        public double End => T0 + Cells.Length * (double)Step;
    }

    /// <param name="walkable">Whether a cell's walk value is ≥ the mover's size + 1 (all cells when null).</param>
    /// <param name="speed">Walking speed in cells per second (Pathfinding base_speed 37 × movement speed).</param>
    /// <param name="blockers">The blocking objects now standing (NPCs, the stash).</param>
    /// <param name="size">The mover's Positioned size (object_size − 1: the player is 1).</param>
    /// <param name="stationary">The blockers that never move (NPCs, the stash), without the living bodies: used when the
    /// living bodies leave no path at all (see Walk).</param>
    public PlayerWalk(int x, int y, double speed, Func<int, int, bool>? walkable = null, int width = 4096, int height = 4096,
        Func<IReadOnlyList<ClientPathfinder.Blocker>>? blockers = null, int size = 1,
        Func<IReadOnlyList<ClientPathfinder.Blocker>>? stationary = null)
    {
        _walkable = walkable ?? ((_, _) => true);
        _blockers = blockers ?? (() => []);
        _stationary = stationary;
        (_w, _h, _size) = (width, height, size);
        _speed = speed;
        _rest = (x, y);
    }

    public double Speed => _speed;

    /// <summary>A bounded escape through estimated living bodies, only until this walk ends or is replaced.</summary>
    public bool EscapingCrowd(double now) => _escapingCrowd && now < ArrivesAt;

    /// <summary>Changes the walking speed. As in the client, the segment in progress keeps its pace; the following
    /// segments are set up at the new speed.</summary>
    public void SetSpeed(double speed, double now)
    {
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        _speed = speed;
        int i = _segments.FindIndex(s => now < s.End);
        if (i < 0) return;
        for (int k = i + 1; k < _segments.Count; k++)
        {
            var s = _segments[k];
            _segments[k] = s with { T0 = _segments[k - 1].End, Step = StepTime(s.From, s.Cells[^1]) };
        }
    }

    /// <summary>The walker's cell at <paramref name="now"/> (seconds, any monotonic clock).</summary>
    public (int X, int Y) Position(double now) => At(now, out _);

    private (int X, int Y) At(double now, out double fraction)
    {
        fraction = 0;
        foreach (var s in _segments)
        {
            if (now >= s.End) continue;
            double steps = Math.Max(0, (now - s.T0) / s.Step);
            int done = (int)steps;
            fraction = steps - done;
            return done == 0 ? s.From : s.Cells[done - 1];
        }
        return _segments.Count == 0 ? _rest : _segments[^1].Cells[^1];
    }

    /// <summary>A new walk order at <paramref name="now"/> (a Move click, C→S 0x25). Returns where the walker is now
    /// (what the echo must report).</summary>
    public (int X, int Y) Order(int targetX, int targetY, double now) => Walk((targetX, targetY), 0, -1, now);

    /// <summary>Puts the walker at a cell (a teleport, e.g. S→C 0x1b7): no walk in progress.</summary>
    public void Teleport(int x, int y, double now)
    {
        _segments = [];
        _rest = (x, y);
        _restSince = now;
        _escapingCrowd = false;
    }

    /// <summary>When the current walk ends (when it ended, once the walker stands).</summary>
    public double ArrivesAt => _segments.Count == 0 ? _restSince : _segments[^1].End;

    /// <summary>Where the current walk ends.</summary>
    public (int X, int Y) Destination => _segments.Count == 0 ? _rest : _segments[^1].Cells[^1];

    /// <summary>
    /// Walks toward a target only until it is within <paramref name="reach"/> cells (a melee attack or a click on an
    /// object: the client walks into range first, the pathfinder's max range). Returns where the walker is now, where it
    /// stops and when; when it is already in reach it stays. <paramref name="tolerance"/> is the target object's size
    /// (ObjectSize − 1), subtracted from the distance before the range test, as the client's MoveTo sends it (a monster:
    /// its ObjectSize − 1; −1 for none). Without it the stop lands up to that many cells closer to the target than the
    /// client's.
    /// </summary>
    public ((int X, int Y) Here, (int X, int Y) Stop, double At) Approach(int targetX, int targetY, double reach, double now,
        int tolerance = -1)
    {
        var here = Walk((targetX, targetY), (int)reach, tolerance, now);
        return (here, Destination, ArrivesAt);
    }

    /// <summary>
    /// Walks up to an object to use it (C→S 0x24 on an NPC, the stash, a waypoint, an area transition), as the client
    /// does: max range the object's size + 3 and the size as the tolerance (live 2026-10-21: an NPC, size 4, walks with
    /// max 7, tolerance 4; the stash, 2, with 5 and 2; a waypoint, 5, with 8 and 5).
    /// </summary>
    /// <param name="objectSize">The object's Positioned size (object_size − 1).</param>
    public ((int X, int Y) Here, (int X, int Y) Stop, double At) WalkUpTo(int targetX, int targetY, int objectSize, double now)
    {
        var here = Walk((targetX, targetY), objectSize + 3, objectSize, now);
        return (here, Destination, ArrivesAt);
    }

    /// <summary>Whether a cell is in walk-up range of an object (the pathfinder's goal test for <see cref="WalkUpTo"/>:
    /// the distance less the object's size within the size + 3).</summary>
    public static bool InWalkUpRange((int X, int Y) cell, (int X, int Y) target, int objectSize)
    {
        int dx = cell.X - target.X, dy = cell.Y - target.Y;
        return MathF.Max(MathF.Sqrt((uint)(dx * dx + dy * dy)) - objectSize, 0f) <= objectSize + 3;
    }

    /// <summary>Walk-up range for an object that may stand where nobody can walk (The Reliquary's Pearl Case on its
    /// pedestal, 7 cells from the nearest floor): in <see cref="InWalkUpRange"/>, or no farther than the nearest walkable
    /// cell within <paramref name="search"/> cells is, plus the usual reach of 3.</summary>
    public static bool InReach((int X, int Y) cell, (int X, int Y) target, int objectSize, Func<int, int, bool>? walkable, int search = 16)
    {
        if (InWalkUpRange(cell, target, objectSize)) return true;
        if (walkable is null) return false;
        double nearest = double.MaxValue;
        for (int y = target.Y - search; y <= target.Y + search; y++)
            for (int x = target.X - search; x <= target.X + search; x++)
                if (walkable(x, y)) nearest = Math.Min(nearest, Math.Sqrt((double)(x - target.X) * (x - target.X) + (double)(y - target.Y) * (y - target.Y)));
        double here = Math.Sqrt((double)(cell.X - target.X) * (cell.X - target.X) + (double)(cell.Y - target.Y) * (cell.Y - target.Y));
        return nearest < double.MaxValue && here <= nearest + 3;
    }

    private (int X, int Y) Walk((int X, int Y) target, int maxRange, int tolerance, double now)
    {
        var here = At(now, out double fraction);
        _escapingCrowd = false;
        var bodies = _blockers();
        var path = ClientPathfinder.FindPath(here, target, _walkable, _w, _h, bodies, _size, 0, maxRange, tolerance,
            ClientPathfinder.SearchLimit.Player, out bool reached);
        // Monster positions here are the server's estimates, and a crowd around the player can leave no gap in them while
        // the client, which sees the bodies where they really are, walks out. Standing still would pull the player back
        // into the crowd on every click: follow the client through the bodies instead, still around walls and objects.
        var end = path.Count == 0 ? here : path[^1];
        bool trapped = Math.Max(Math.Abs(end.X - here.X), Math.Abs(end.Y - here.Y)) <= _size + 1;
        if (!reached && trapped && _stationary is not null
            && ClientPathfinder.FindPath(here, target, _walkable, _w, _h, _stationary(), _size,
                0, maxRange, tolerance, ClientPathfinder.SearchLimit.Player, out bool reachedPast) is var past
            && reachedPast && past.Count > 1
            && !ClientPathfinder.ObjectBlocks(bodies, past[^1].X, past[^1].Y, _size + 1, _size + 4))
        {
            path = past;
            _escapingCrowd = true;
        }
        _segments = [];
        _rest = here;
        _restSince = now;
        if (path.Count < 2) return here;
        double t = now;
        for (int i = 1; i < path.Count; i++)
        {
            var cells = ClientPathfinder.Line(path[i - 1], path[i]).Skip(1).ToArray();
            float step = StepTime(path[i - 1], path[i]);
            // The first step comes when the step in progress would have: its elapsed fraction carries over.
            if (i == 1) t -= fraction * step;
            _segments.Add(new Segment(path[i - 1], cells, t, step));
            t += cells.Length * (double)step;
        }
        return here;
    }

    // 0x141e4d180: the segment's time is its length over the speed, spread evenly over its major-axis steps.
    private float StepTime((int X, int Y) a, (int X, int Y) b)
    {
        int dx = b.X - a.X, dy = b.Y - a.Y;
        float total = MathF.Sqrt(dx * dx + dy * dy) / (float)_speed;
        return total / Math.Max(Math.Abs(dx), Math.Abs(dy));
    }
}
