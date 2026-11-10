namespace GameServer.Core.World;

/// <summary>
/// The server's copy of the local player's client-predicted walk (Agents/docs/protocol/07-latency-visibility-movement.md
/// §5). Each order takes the client's own path (<see cref="ClientPathfinder"/>) from the player's cell, and the walker
/// moves as the client's Positioned does (0x141e34400 → 0x141e4d180, ticked by 0x141e32710 → 0x141e4d390): every
/// segment between two waypoints is a Bresenham line of cells, one step per (|segment| / speed) / (major-axis steps)
/// seconds, so the cell is always a whole grid cell. A new order mid-step keeps the step's elapsed fraction (the
/// client rescales its time accumulator to the new step time). Each step is checked against terrain and bodies by the
/// Gateway's step pass, which replays all walkers together, on a lockstep client frame by frame in the client's object
/// order (<see cref="Steps"/>, <see cref="StopAt"/>, <see cref="Repath"/>).
/// The 0x1ab echo reports <see cref="Position"/>; the client snaps to it only when it is ~92+ cells off (Actor handler
/// 0x1421940a0).
/// </summary>
public sealed class PlayerWalk
{
    private readonly Func<int, int, bool> _walkable;
    private readonly Func<int, int, int>? _sight;
    private readonly Func<IReadOnlyList<ClientPathfinder.Blocker>> _blockers;
    private readonly int _w, _h, _size;
    private readonly ClientPathfinder.SearchLimit _limit;
    private double _speed;
    private List<Segment> _segments = [];
    private (int X, int Y) _rest;
    private double _restSince;
    private (int X, int Y) _target;
    private int _maxRange, _tolerance = -1;
    // The current order's precision (MoveTo +0x528): the projectile grid value its line to the target needs; 1 again once
    // a search above 1 found nothing (0x141e7b920), for the rest of the order.
    private int _precision = 1;
    // The client's step accumulator (+0x244) on a walker that a stop left standing mid-step. The stop waits for the
    // frame's steps (0x141e350e0), and the tick after them clears only the step time (0x141e32710), so the time since
    // the last step stays; a forced set keeps it on a standing walker (0x141e31800 zeroes it only on one under way), and
    // the next walk takes it as it is, with no step time to rescale it from (0x141e4d180). Zero while walking and after
    // a walk the step loop ended (its path's end, a blocked step). From: the end of the tick that stops the walk, from
    // which it holds; a walk set up before then, in the stop's own frame, does not take it (Walk).
    private (double Seconds, double From) _kept;

    // Joined: the next segment goes on along the same piece of the path (steps already due, split off by InFrame), so
    // this one's last cell is no waypoint (EndsPiece).
    private sealed record Segment((int X, int Y) From, (int X, int Y)[] Cells, double T0, float Step, bool Joined = false)
    {
        public double End => T0 + Cells.Length * (double)Step;
    }

    /// <param name="walkable">Whether a cell's walk value is ≥ the mover's size + 1 (all cells when null).</param>
    /// <param name="speed">Walking speed in cells per second (Pathfinding base_speed 37 × movement speed).</param>
    /// <param name="blockers">The blocking objects now standing (NPCs, the stash).</param>
    /// <param name="size">The mover's Positioned size (object_size − 1: the player is 1).</param>
    /// <param name="limit">The client's search budget for this mover: the player's by default, a monster's by its rarity.</param>
    /// <param name="sight">The projectile grid (the client's walk layer 1, 0 … 5): what an order's line to its target is
    /// tested on (<see cref="ClientPathfinder.InReach"/>). Null: no line test, any cell in range is a goal.</param>
    public PlayerWalk(int x, int y, double speed, Func<int, int, bool>? walkable = null, int width = 4096, int height = 4096,
        Func<IReadOnlyList<ClientPathfinder.Blocker>>? blockers = null, int size = 1,
        ClientPathfinder.SearchLimit limit = ClientPathfinder.SearchLimit.Player, Func<int, int, int>? sight = null)
    {
        _walkable = walkable ?? ((_, _) => true);
        _sight = sight;
        _blockers = blockers ?? (() => []);
        (_w, _h, _size, _limit) = (width, height, size, limit);
        _speed = speed;
        _rest = (x, y);
    }

    public double Speed => _speed;

    /// <summary>The frame a time's messages take effect in on this client, if it runs on frames: a lockstep client applies
    /// them at the frame's start, and its walkers only move on frames.</summary>
    public Func<double, (double Start, double End)?>? FrameOf { get; init; }

    private double Frame(double now) => FrameOf?.Invoke(now)?.Start ?? now;

    /// <summary>The mover's Positioned size.</summary>
    public int Size => _size;

    /// <summary>Whether a cell's walk value lets this mover stand on it.</summary>
    public bool Floor(int x, int y) => _walkable(x, y);

    /// <summary>Whether the current path ends in the order's range. A path the search cut short is walked to its end, and
    /// then the client paths again from there (<see cref="Repath"/>; 0x141e7bec0 → 0x141e7b920).</summary>
    public bool Reached { get; private set; } = true;

    /// <summary>Whether the walk ended at its goal: the search found the goal and every step was taken. False for a walk
    /// the search cut short, one that found no path (<see cref="Reached"/> is set as there is no step to take), or one
    /// a blocked step ended (<see cref="StopAt"/>): the walker stands, but not where it was going.</summary>
    public bool ReachedGoal => Reached && _goalReached;
    private bool _goalReached = true;

    /// <summary>The path began on a blocked cell (0x141e4d890's out flag → Positioned +0x1e5 &amp; 4): the walker steps
    /// through bodies until its first free step; terrain still stops it (0x141e32710).</summary>
    public bool WalksThrough { get; set; }

    /// <summary>The steps of the current walk after <paramref name="from"/> up to <paramref name="to"/>: when each cell is
    /// entered, in order, as <see cref="Position"/> reports them. A hold delays them.</summary>
    public List<(double At, (int X, int Y) Cell)> Steps(double from, double to)
    {
        var steps = new List<(double, (int, int))>();
        foreach (var s in _segments)
        {
            if (s.End <= from) continue;
            for (int k = 1; k <= s.Cells.Length; k++)
            {
                double at = s.T0 + k * (double)s.Step;
                if (at > to) return steps;
                if (at > from) steps.Add((at, s.Cells[k - 1]));
            }
        }
        return steps;
    }

    /// <summary>Ends the walk before its step at <paramref name="at"/> (a time from <see cref="Steps"/>): the client found
    /// that step blocked and stands where it is, with no new path (0x141e32710, whose blocked callback ends the pathing:
    /// 0x141e7bc20). The steps before stay, so a position asked for an earlier time is still the walked cell.</summary>
    public void StopAt(double at)
    {
        var here = At(at - 1e-9, out _);
        var kept = new List<Segment>();
        foreach (var s in _segments)
        {
            if (s.End < at - 1e-9) { kept.Add(s); continue; }
            int k = (int)Math.Round((at - s.T0) / s.Step);
            if (k > 1) kept.Add(s with { Cells = s.Cells[..(k - 1)] });
            break;
        }
        _rest = here;
        _restSince = at;
        _segments = kept;
        _kept = default;
        Reached = true;
        _goalReached = false;
        WalksThrough = false;
    }

    /// <summary>Whether the step at <paramref name="at"/> (a time from <see cref="Steps"/>) ends a piece of the path: the
    /// walker steps into one of its waypoints, where the client's MoveTo tests its goal (0x141e7bec0).</summary>
    public bool EndsPiece(double at)
    {
        foreach (var s in _segments)
            if (s.Cells.Length > 0 && s.End == at) return !s.Joined;
        return false;
    }

    /// <summary>Ends the walk at its goal once it has taken its steps up to <paramref name="until"/> (a lockstep client's
    /// MoveTo found its goal, 0x141e7bc20). Mid-piece the stop (0x141e350e0) waits for the next tick, whose due steps the
    /// walker still takes: <paramref name="until"/> is that tick's end. At a waypoint the piece's step time is already
    /// spent and the walker stands there at once: <paramref name="until"/> is that step's time. The steps up to then stay,
    /// so a position asked for an earlier time is still the walked cell, and the time since the last step stays for the
    /// next walk as after any stop that waits for a tick (the stop clears only the step time, 0x141e32710); none when the
    /// walk's own steps ended by then (the step loop ended it). A stop that waits for the tick from
    /// <paramref name="from"/> ends at the first waypoint that tick steps into, as at a path's end: the walker stands
    /// there, does not go on into the next piece, and keeps nothing of the step time (<see cref="WaypointIn"/>).</summary>
    public void StopAfter(double until, double? from = null)
    {
        bool atWaypoint = false;
        if (from is { } tick && WaypointIn(tick, until) is { } waypoint) (until, atWaypoint) = (waypoint, true);
        var kept = new List<Segment>();
        double last = _segments.Count > 0 ? _segments[0].T0 : until;
        bool beyond = false;
        foreach (var s in _segments)
        {
            int k = 0;
            while (k < s.Cells.Length && s.T0 + (k + 1) * (double)s.Step <= until) k++;
            if (k > 0)
            {
                kept.Add(k == s.Cells.Length ? s : s with { Cells = s.Cells[..k] });
                last = s.T0 + k * (double)s.Step;
            }
            if (k < s.Cells.Length)
            {
                beyond = true;
                break;
            }
        }
        _rest = kept.Count > 0 ? kept[^1].Cells[^1] : _segments.Count > 0 ? _segments[0].From : _rest;
        _restSince = until;
        _segments = kept;
        _kept = beyond && !atWaypoint ? (Math.Max(0, until - last), until) : default;
        Reached = true;
        _goalReached = true;
        WalksThrough = false;
    }

    // When the tick from `from` to `until` steps the walker into a waypoint (the end of a piece of its path), if it does.
    // A stop pending in that tick ends the walk there: the client's step loop finds the piece done and no next one set
    // up, as at a path's end, and leaves the accumulator at zero (tick traces 2026-11-10: 19 of 19 such stops, where 31
    // of 31 that ended mid-piece kept the time since their last step).
    private double? WaypointIn(double from, double until)
    {
        foreach (var s in _segments)
        {
            if (s.Cells.Length == 0 || s.End <= from) continue;
            if (s.End > until) break;
            if (!s.Joined) return s.End;
        }
        return null;
    }

    /// <summary>Ends the walk for an action that cancels it at <paramref name="now"/> (a hit's recovery, 0x141e95e60): the
    /// cancelled Move stops its walker with the same stop (0x141e350e0), which waits for the tick. A walker on a client
    /// that runs on frames still takes the steps due in that frame and keeps the time since its last one
    /// (<see cref="StopAfter"/>); without frames it stands where it is. Returns where it stands.</summary>
    public (int X, int Y) Cancel(double now)
    {
        if (FrameOf?.Invoke(now) is { } frame)
        {
            // A walker already standing has no step time for the stop to act on (0x141e350e0 does nothing then): the
            // time an earlier stop left it stays for its next walk.
            if (Moving(frame.Start)) StopAfter(frame.End, frame.Start);
        }
        else
        {
            var here = At(now, out _);
            Teleport(here.X, here.Y, now);
        }
        return _rest;
    }

    /// <summary>Paths again to the current order's target from where the walk ended, at <paramref name="at"/>.</summary>
    public (int X, int Y) Repath(double at) => Walk(_target, _maxRange, _tolerance, at, _precision);

    /// <summary>Whether a cell meets the current order's goal for a target (0x141e7b260, what the client's action tests
    /// before it swings): within <paramref name="reach"/> of it, the target's size (<paramref name="tolerance"/>, −1 for
    /// none) widening the reach, and with the line to the target's body on the projectile grid at the order's precision
    /// (<see cref="ClientPathfinder.InReach"/>).</summary>
    public bool InReachOf((int X, int Y) from, (int X, int Y) target, int reach, int tolerance) =>
        ClientPathfinder.InReach(from, target, 0, reach, tolerance, _sight, _precision);

    /// <summary>The current order's precision (MoveTo +0x528): 1 again once a search above 1 found nothing.</summary>
    public int Precision => _precision;

    /// <summary>The target's cell the current order last searched toward (MoveTo +0x550, which 0x141e7b920 stores).</summary>
    public (int X, int Y) SearchedFor => _target;

    /// <summary>Changes the walking speed. As in the client, the segment in progress starts again from the walker's cell
    /// at the new pace, at once (a held walk when its hold ends), and the following segments are set up at the new speed.</summary>
    public void SetSpeed(double speed, double now)
    {
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        _speed = speed;
        // On frames the message takes effect at the frame's start, on the cell the walker stands on then.
        now = Frame(now);
        var here = At(now, out double fraction);
        int i = _segments.FindIndex(s => now < s.End);
        if (i < 0) return;
        // The piece in progress, to its waypoint: steps already due were split off it (Joined), and go with it again.
        int last = i;
        while (_segments[last].Joined && last + 1 < _segments.Count) last++;
        var left = _segments[i];
        var end = _segments[last].Cells[^1];
        if (end == here) return;
        float step = StepTime(here, end);
        double start = now < left.T0 ? left.T0 : now - fraction * step;
        var walk = new List<Segment> { new(here, [.. ClientPathfinder.Line(here, end).Skip(1)], start, step) };
        foreach (var next in _segments.Skip(last + 1))
            walk.Add(next with { T0 = walk[^1].End, Step = StepTime(next.From, next.Cells[^1]) });
        _segments = walk;
    }

    /// <summary>Lays the piece in progress again from the walker's cell at the speed it has. The client does this to a
    /// walking actor for every stats message that names a movement stat, changed or not (its stat listener 0x141e9cf90
    /// runs the active MoveTo's piece again, 0x141e7b6f0 → 0x141e34400): the straight line from here to the waypoint can
    /// step sideways at another cell than the line from where the piece began.</summary>
    public void Relay(double now) => SetSpeed(_speed, now);

    /// <summary>The walker's cell at <paramref name="now"/> (seconds, any monotonic clock).</summary>
    public (int X, int Y) Position(double now) => At(Frame(now), out _);

    /// <summary>Where the current walk has the walker <paramref name="seconds"/> after <paramref name="now"/>: a cell
    /// ahead on its path. <see cref="Position"/> cannot answer that on frames: a time past the current frame reads as the
    /// frame's start (the walker has not stepped there yet).</summary>
    public (int X, int Y) Ahead(double now, double seconds) => At(Frame(now) + seconds, out _);

    /// <summary>The walk as it stands, to put back with <see cref="Restore"/> when an order turns out not to be taken.</summary>
    public Snapshot Save() => new(this);

    /// <summary>Puts back a walk saved with <see cref="Save"/>.</summary>
    public void Restore(Snapshot saved) => saved.Into(this);

    /// <summary>A walk's state (<see cref="Save"/>).</summary>
    public sealed class Snapshot
    {
        readonly List<Segment> _segments;
        readonly (int X, int Y) _rest, _target;
        readonly double _restSince;
        readonly (double, double) _kept;
        readonly int _maxRange, _tolerance, _precision;
        readonly bool _reached, _goalReached, _walksThrough;

        internal Snapshot(PlayerWalk w) =>
            (_segments, _rest, _target, _restSince, _kept, _maxRange, _tolerance, _precision, _reached, _goalReached, _walksThrough) =
            (w._segments, w._rest, w._target, w._restSince, w._kept, w._maxRange, w._tolerance, w._precision, w.Reached, w._goalReached, w.WalksThrough);

        internal void Into(PlayerWalk w)
        {
            (w._segments, w._rest, w._target, w._restSince, w._kept, w._maxRange, w._tolerance, w._precision) =
                (_segments, _rest, _target, _restSince, _kept, _maxRange, _tolerance, _precision);
            (w.Reached, w._goalReached, w.WalksThrough) = (_reached, _goalReached, _walksThrough);
        }
    }

    /// <summary>Where the walker is within its current step at <paramref name="now"/>: the seconds since the step before
    /// (the client's accumulator, +0x244) and the step's length; (0, 0) when it stands or is held.</summary>
    public (double Into, double Step) StepPhase(double now) => Phase(Frame(now));

    private (double Into, double Step) Phase(double now)
    {
        foreach (var s in _segments)
        {
            if (now >= s.End) continue;
            if (now < s.T0) return (0, 0);
            double steps = (now - s.T0) / s.Step;
            return ((steps - Math.Floor(steps)) * s.Step, s.Step);
        }
        return (0, 0);
    }

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
        Reached = true;
        _goalReached = true;
        WalksThrough = false;
    }

    /// <summary>Holds the walker where it is for <paramref name="seconds"/>; then the walk goes on, as the client's does
    /// after a hit's recovery. A hold already running that lasts longer keeps its end.</summary>
    public void Pause(double now, double seconds)
    {
        // As a stop: the hold starts after the frame's steps.
        if (FrameOf?.Invoke(now) is { } frame) (now, seconds) = (frame.End, seconds - (frame.End - frame.Start));
        var here = At(now, out double fraction);
        int i = _segments.FindIndex(s => now < s.End);
        if (i < 0) return;
        var left = _segments[i];
        int done = Math.Max(0, (int)((now - left.T0) / left.Step));
        var walk = new List<Segment> { left with { From = here, Cells = left.Cells[done..], T0 = Math.Max(left.T0, now + seconds - fraction * left.Step) } };
        foreach (var next in _segments.Skip(i + 1)) walk.Add(next with { T0 = walk[^1].End });
        _segments = walk;
    }

    /// <summary>When the current walk ends (when it ended, once the walker stands), holds included.</summary>
    public double ArrivesAt => _segments.Count == 0 ? _restSince : _segments[^1].End;

    /// <summary>Whether the walker is under way: it has a walk left and is not held (<see cref="Pause"/>).</summary>
    public bool Moving(double now)
    {
        var walk = _segments;
        return walk.Count > 0 && walk[0].T0 <= now && now < walk[^1].End;
    }

    /// <summary>Where the current walk ends.</summary>
    public (int X, int Y) Destination => _segments.Count == 0 ? _rest : _segments[^1].Cells[^1];

    /// <summary>
    /// Walks toward a target only until it is within <paramref name="reach"/> cells (a melee attack or a click on an
    /// object: the client walks into range first, the pathfinder's max range). Returns where the walker is now, where it
    /// stops and when; when it is already in reach it stays, on frames where the frame's steps leave it (the stop waits
    /// for them, 0x141e350e0). <paramref name="tolerance"/> is the target object's size
    /// (ObjectSize − 1), subtracted from the distance before the range test, as the client's MoveTo sends it (a monster:
    /// its ObjectSize − 1; −1 for none). Without it the stop lands up to that many cells closer to the target than the
    /// client's. <paramref name="precision"/> is the order's: the projectile grid value every cell of the line from the
    /// stop to the target must hold (1 for a melee attack, 3 for a bow's), when the walk has the grid.
    /// </summary>
    public ((int X, int Y) Here, (int X, int Y) Stop, double At) Approach(int targetX, int targetY, double reach, double now,
        int tolerance = -1, int precision = 1)
    {
        var here = Walk((targetX, targetY), (int)reach, tolerance, now, precision);
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
        return (here, _segments.Count == 0 ? here : Destination, ArrivesAt);
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

    /// <summary>
    /// Shield Charge's run (setup 0x141a6bae0): no search, a straight line to the aim cut to <paramref name="range"/> cells.
    /// An aim off the floor moves back along the line to the first cell the mover can stand on. The steps are checked as
    /// any walk's are, so the first blocked one ends the run.
    /// </summary>
    public (int X, int Y) Charge(int targetX, int targetY, int range, double now)
    {
        var here = At(now, out double fraction);
        double length = Math.Sqrt((double)(targetX - here.X) * (targetX - here.X) + (double)(targetY - here.Y) * (targetY - here.Y));
        (int X, int Y) end = (targetX, targetY);
        if (length > range)
            end = (here.X + (int)((targetX - here.X) * (double)range / length), here.Y + (int)((targetY - here.Y) * (double)range / length));
        if (!_walkable(end.X, end.Y))
            end = ClientPathfinder.Line(end, here).FirstOrDefault(c => _walkable(c.X, c.Y), here);
        (_target, _maxRange, _tolerance) = (end, 0, -1);
        // From a stand the run starts a whole step after the use, with no kept time: its setup goes through 0x141e9a3c0,
        // not the MoveTo the kept time was traced on, and its first step from a stand is not traced.
        Follow(end == here ? [here] : [here, end], here, fraction, 0, now);
        Reached = true;
        WalksThrough = false;
        return here;
    }

    private (int X, int Y) Walk((int X, int Y) target, int maxRange, int tolerance, double now, int precision = 1)
    {
        now = Frame(now);
        var here = At(now, out double fraction);
        var bodies = _blockers();
        _precision = precision;
        // MoveTo tries the goal where the walker stands first (0x141e7b260): in range with the line to the target's body,
        // there is nothing to walk.
        bool reached = ClientPathfinder.InReach(here, target, 0, maxRange, tolerance, _sight, precision);
        List<(int X, int Y)> path = [];
        if (!reached)
        {
            path = ClientPathfinder.FindPath(here, target, _walkable, _w, _h, bodies, _size, 0, maxRange, tolerance, _limit, _sight, precision, out reached);
            // Nothing found at a precision above 1: the goal is tried where the walker stands at 1, which the order then
            // keeps (0x141e7b920); the client shoots from here when that holds.
            if (!reached && path.Count == 0 && precision > 1)
            {
                _precision = 1;
                reached = ClientPathfinder.InReach(here, target, 0, maxRange, tolerance, _sight, 1);
            }
        }
        (_target, _maxRange, _tolerance) = (target, maxRange, tolerance);
        // No step to take: the client ends the action instead of pathing again (0x141e7b920 with nothing found).
        Reached = reached || path.Count < 2;
        _goalReached = reached;
        WalksThrough = !_walkable(here.X, here.Y) || ClientPathfinder.ObjectBlocks(bodies, here.X, here.Y, _size + 1, _size + 4);
        // A stop takes effect after the frame's steps (0x141e350e0). The caller reports here, where the client is now; the
        // walker stands where the client will, or the next start puts the client back up to two cells, and keeps the time
        // since its last step for the next walk (_kept).
        if (path.Count < 2 && Moving(now))
        {
            if (FrameOf?.Invoke(now) is { } frame)
            {
                // On frames the steps still due in the tick stay steps of the walk, so the step pass checks each against
                // the bodies as the client's tick does (a blocked one ends the walk where it stands, nothing kept), and
                // the tick ends the walk at a waypoint it steps into (StopAfter).
                StopAfter(frame.End, now);
                _goalReached = reached;
                return here;
            }
            // Predictive uses a continuous clock: its frame boundary is unknown, but the same stop retains time.
            double stopAt = now;
            var stop = At(stopAt, out _);
            var kept = (Phase(stopAt).Into, stopAt);
            Teleport(stop.X, stop.Y, now);
            // At its goal only when it was in range where it stood, not when the search found no path (ReachedGoal).
            _goalReached = reached;
            _kept = kept;
            return here;
        }
        // The stop's own frame went to the old walk's steps already: a walk set up in it takes no kept time.
        Follow(path, here, fraction, now >= _kept.From ? _kept.Seconds : 0, now);
        return here;
    }

    // `kept`: the time since the last step that a walk from a stand starts with.
    private void Follow(IReadOnlyList<(int X, int Y)> path, (int X, int Y) here, double fraction, double kept, double now)
    {
        bool standing = !Moving(now);
        var walk = new List<Segment>();
        double t = now;
        for (int i = 1; i < path.Count; i++)
        {
            var cells = ClientPathfinder.Line(path[i - 1], path[i]).Skip(1).ToArray();
            float step = StepTime(path[i - 1], path[i]);
            // The first step comes when the step in progress would have: its elapsed fraction carries over. From a stand,
            // the time a stop kept carries over as it is.
            if (i == 1) t -= standing ? kept : fraction * step;
            walk.Add(new Segment(path[i - 1], cells, t, step));
            t += cells.Length * (double)step;
        }
        if (walk.Count > 0) _kept = default;
        // Whole: monsters' walks are read outside the lock too (Rampage's weapons).
        _rest = here;
        _restSince = now;
        _segments = InFrame(walk, now);
    }

    // A kept time of a step or more leaves steps due before the order's frame. The client's first tick takes them all, as
    // its loop steps while the accumulator holds a step (0x141e32710), so they go in the order's frame, in order, before
    // the walk's first step not yet due and the frame's end (evenly: the step pass reads only a step's frame and order),
    // not before the frame, where no frame's pass would check them and the frame's start would show them.
    private List<Segment> InFrame(List<Segment> walk, double now)
    {
        static int Due(Segment s, double at)
        {
            int n = 0;
            while (n < s.Cells.Length && s.T0 + (n + 1) * (double)s.Step <= at) n++;
            return n;
        }
        int due = 0;
        double next = double.PositiveInfinity;
        foreach (var s in walk)
        {
            int d = Due(s, now);
            due += d;
            if (d < s.Cells.Length)
            {
                next = s.T0 + (d + 1) * (double)s.Step;
                break;
            }
        }
        if (due == 0) return walk;
        float gap;
        if (FrameOf?.Invoke(now) is { } frame)
        {
            if (frame.End <= now) return walk;
            gap = (float)((Math.Min(next, frame.End) - now) / (due + 1));
        }
        else
        {
            // With no known client frame, retain the continuous clock. Due steps belong just after the order, so its
            // origin stays put and the collision pass sees every step. Use clock precision, not an invented tick delay.
            gap = Math.Max(float.Epsilon, (float)(Math.BitIncrement(now) - now));
        }
        var placed = new List<Segment>();
        int before = 0;
        foreach (var s in walk)
        {
            int d = Due(s, now);
            if (d == 0)
            {
                placed.Add(s);
                continue;
            }
            placed.Add(new Segment(s.From, s.Cells[..d], now + before * (double)gap, gap, Joined: d < s.Cells.Length));
            if (d < s.Cells.Length) placed.Add(s with { From = s.Cells[d - 1], Cells = s.Cells[d..], T0 = s.T0 + d * (double)s.Step });
            before += d;
        }
        return placed;
    }

    // 0x141e4d180: the segment's time is its length over the speed, spread evenly over its major-axis steps.
    private float StepTime((int X, int Y) a, (int X, int Y) b)
    {
        int dx = b.X - a.X, dy = b.Y - a.Y;
        float total = MathF.Sqrt(dx * dx + dy * dy) / (float)_speed;
        return total / Math.Max(Math.Abs(dx), Math.Abs(dy));
    }
}
