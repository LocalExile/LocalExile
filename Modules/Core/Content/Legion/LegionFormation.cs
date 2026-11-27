using GameServer.Core;

namespace GameServer.Modules.Legion;

/// <summary>
/// Where the members of one army stand. Both armies are blocks that face each other across the monolith:
/// the axis is a random direction through the monolith, the contact line is perpendicular to it through the monolith, army 0
/// lies on one side and army 1 on the other, each a block of width W (along the contact line) and depth D (away from it).
/// A member's FromPct..ToPct band maps to its distance from the front: 0% is the front edge, 100% the rear; the general
/// (90-100) stands at the far rear in the middle. The disciplined factions (Eternal Empire, Templar) stand in rows and columns
/// facing the enemy; the others (Karui, Vaal, Maraketh, Faridun) in loose groups. Per W2 (the real game's look) soldiers never
/// overlap and an army covers more than a screen. EVERY CONSTANT HERE IS A GUESS: tune after the live check.
/// </summary>
sealed class LegionFormation
{
    /// <summary>Orderly factions (Eternal Empire, Templar: LegionFactions.__col3 == 0.95) stand in straight rows and columns;
    /// looser factions (Vaal/Maraketh/Faridun 0.65, Karui 0.50) stand in scattered groups.</summary>
    static bool IsOrderly(LegionArmy.FactionPlan plan)
    {
        var faction = LegionData.Current.Factions.FirstOrDefault(f => f.Row == plan.Faction || f.Id == plan.Id);
        return faction is not null ? faction.Orderliness >= 0.9 : plan.Id is "EternalEmpire" or "Templar";
    }

    /// <summary>The gap between the two armies' front edges, in spacings: each front stands half of it from the contact line (tune after live check).</summary>
    const double FrontGapSpacings = 1.5;
    /// <summary>Centre-to-centre spacing of rows: max(minimum, ObjectSize x factor) cells. ObjectSize is the collision size; the drawn
    /// soldier with its crystal coat is 6-8 cells across (tune after live check).</summary>
    const double GridSpacingMin = 15, GridSpacingPerBody = 5.6;
    /// <summary>Minimum distance of two loose members: max(minimum, mean of the two ObjectSizes x factor) cells (tune after live check).</summary>
    const double LooseSpacingMin = 16, LooseSpacingPerBody = 6.2;
    /// <summary>Each loose member draws its minimum distance factor in this range, so the gaps differ (tune after live check).</summary>
    const double LooseFactorMin = 0.85, LooseFactorMax = 1.4;
    /// <summary>A body's size in cells is its variety's ObjectSize, clamped here (tune after live check).</summary>
    const int MinBody = 2, MaxBody = 10;
    /// <summary>Block width over depth (tune after live check).</summary>
    const double WidthOverDepth = 1.75;
    /// <summary>Cell area per member, in squares of the spacing: a loose group needs room for random sampling (the packing
    /// limit is about 1.4); a grid needs just over 1 (tune after live check).</summary>
    const double RandomArea = 2.2, GridArea = 1.15;
    /// <summary>The shallowest block, in cells (tune after live check).</summary>
    const double MinDepth = 10;
    /// <summary>Random spots tried per member before the member is skipped, and the nearby cells tried around a blocked grid slot.</summary>
    const int Tries = 30;
    static readonly (int X, int Y)[] Nearby = [(1, 0), (-1, 0), (0, 1), (0, -1), (2, 0), (-2, 0), (0, 2), (0, -2)];
    /// <summary>Two grid members closer than this fraction of the row spacing count as overlapping (jitter and nearby cells can bring them together; tune after live check).</summary>
    const double GridOverlapFraction = 0.35;
    /// <summary>Grid jitter and the general's sideways wobble, in fractions of the spacing (tune after live check).</summary>
    const double GridJitter = 0.3, GeneralSpread = 1.0;

    readonly Player _player;
    readonly Random _rng;
    readonly bool _orderly;
    readonly int _cx, _cy;
    readonly double _ux, _uy, _vx, _vy;   // the unit vector away from the monolith along the axis (this army's side), and the one along the contact line
    readonly double _depth, _width, _pitch;
    readonly int _maxBody;
    readonly double _frontGap;
    // random groups: a spatial hash of the placed bodies (cell = the biggest minimum distance, so a neighbour test reads 3x3 cells)
    readonly double _hashCell;
    readonly Dictionary<(int, int), List<(int X, int Y, int Body)>> _placed = [];
    readonly int[] _cursor;   // per row: the next column slot to try (middle outwards)
    readonly int _rows, _cols;

    /// <summary>How the soldiers face: the direction towards the enemy, as an object rotation (Atan2(dx, -dy), as the
    /// other modules turn an object; a guess for monsters, check live).</summary>
    public float Facing { get; }

    /// <summary>The block of an army: its size comes from its member count and spacing. <paramref name="bodyOf"/> gives a
    /// monster row's body size in cells (its ObjectSize); <paramref name="axis"/> is the shared axis angle.</summary>
    public LegionFormation(Player player, Random rng, int cx, int cy, double axis, int army, LegionArmy.FactionPlan plan, Func<int, int> bodyOf)
    {
        _player = player; _rng = rng; _cx = cx; _cy = cy;
        _orderly = IsOrderly(plan);
        double side = army == 0 ? 1 : -1;
        _ux = Math.Cos(axis) * side; _uy = Math.Sin(axis) * side;
        _vx = -_uy; _vy = _ux;
        Facing = (float)Math.Atan2(-_ux, _uy);   // towards the enemy, that is back through the monolith: direction (-ux, -uy)

        // The members' bands: (count, from, to, body).
        var groups = new List<(int N, int From, int To, int Body)>();
        foreach (var s in plan.Squads) groups.Add((s.Count, s.FromPct, s.ToPct, Body(bodyOf(s.Monster))));
        foreach (var c in plan.Chests) groups.Add((1, c.FromPct, c.ToPct, Body(bodyOf(c.Monster))));
        int total = groups.Sum(g => g.N);
        _maxBody = groups.Count == 0 ? MinBody : groups.Max(g => g.Body);
        double mean = total == 0 ? MinBody : groups.Sum(g => (double)g.N * g.Body) / total;
        _pitch = _orderly ? Math.Max(GridSpacingMin, mean * GridSpacingPerBody) : Math.Max(LooseSpacingMin, mean * LooseSpacingPerBody) * (LooseFactorMin + LooseFactorMax) / 2;   // the mean factor, so the block grows with the mean spacing
        _hashCell = Math.Max(LooseSpacingMin, _maxBody * LooseSpacingPerBody) * LooseFactorMax;
        _frontGap = FrontGapSpacings * _pitch / 2;
        double area = (_orderly ? GridArea : RandomArea) * _pitch * _pitch;

        // D from the whole count, and from the most crowded band (a thin band must hold its own members): W = ratio x D.
        double d2 = total * area / WidthOverDepth;
        foreach (var band in groups.GroupBy(g => (g.From, g.To)))
        {
            double fraction = Math.Max(0.05, (band.Key.To - band.Key.From) / 100.0);
            d2 = Math.Max(d2, band.Sum(g => g.N) * area / (WidthOverDepth * fraction));
        }
        _depth = Math.Max(MinDepth, Math.Sqrt(d2));
        _width = _depth * WidthOverDepth;
        _rows = (int)(_depth / _pitch) + 1;
        _cols = (int)(_width / _pitch) + 1;
        _cursor = new int[_rows];
    }

    static int Body(int objectSize) => Math.Clamp(objectSize, MinBody, MaxBody);

    /// <summary>One line for the log: the block's size in cells and the spacing.</summary>
    public string Describe() => $"{(_orderly ? "rows" : "loose")} {_width:0}x{_depth:0} spacing {_pitch:0.#}";

    /// <summary>The cell of one member standing in the band, or null when none is free and walkable (the member is skipped).
    /// <paramref name="body"/> is its size in cells; <paramref name="centred"/> puts it in the middle of the rear (the general).</summary>
    public (int X, int Y)? Place(int fromPct, int toPct, int body, bool centred)
    {
        int walk = body;   // walkability takes the real size; the capped one is for spacing
        body = Body(body);
        return (_orderly ? PlaceOnGrid(fromPct, toPct, body, walk, centred) : PlaceLoose(fromPct, toPct, body, walk, centred))
            ?? PlaceNearest(fromPct, toPct, body, walk);
    }

    /// <summary>The last resort when the band and the block hold no free slot (a block packed by its soldiers): the nearest free walkable
    /// cell to the middle of the band, searched in square rings outwards (a step of 2 cells), so a planned member (a war chest) is not dropped.
    /// Runs only for a member that found no slot, so the cost is rare.</summary>
    (int X, int Y)? PlaceNearest(int from, int to, int body, int walk)
    {
        var (cx, cy) = At((from + to) / 200.0 * _depth, 0);
        int max = (int)Math.Max(_width, _depth) + (int)_pitch;
        for (int r = 0; r <= max; r += 2)
            for (int i = -r; i <= r; i += 2)
                foreach (var (x, y) in r == 0 ? new[] { (cx, cy) } : new[] { (cx + i, cy - r), (cx + i, cy + r), (cx - r, cy + i), (cx + r, cy + i) })
                    if (Open(x, y, walk) && !Crowded(x, y, body)) { Remember(x, y, body); return (x, y); }
        return null;
    }

    (int X, int Y) At(double along, double lateral) =>
        ((int)Math.Round(_cx + _ux * (_frontGap + along) + _vx * lateral), (int)Math.Round(_cy + _uy * (_frontGap + along) + _vy * lateral));

    bool Open(int x, int y, int walk)
    {
        var layout = _player.Layout;
        return x >= 0 && y >= 0 && x < layout.GridW && y < layout.GridH
            && layout.WalkableFor(x, y, Math.Clamp(walk - 1, 0, 127)) && !_player.Barrier.Contains((x, y));
    }

    /// <summary>Rows and columns: a row per spacing from the front, columns from the middle outwards, so a half-filled row stays
    /// centred. The band picks the rows (the nearest row when it holds none). A blocked slot tries the cells next to it, then the next slot.</summary>
    (int X, int Y)? PlaceOnGrid(int from, int to, int body, int walk, bool centred)
    {
        int first = Math.Min(_rows - 1, (int)Math.Ceiling(from / 100.0 * _depth / _pitch));
        int last = Math.Clamp((int)Math.Floor(to / 100.0 * _depth / _pitch), first, _rows - 1);
        if (centred) { first = _rows - 1; last = _rows - 1; }
        int blocked = 0;
        // the band's rows first, then (only when they are full) the rows behind it, then the ones in front, nearest first
        for (int step = 0; step < _rows && blocked < 24; step++)
        {
            int row = step <= last - first ? first + step : step <= _rows - 1 - first ? first + step : first - 1 - (step - (_rows - first));
            if (centred) row = _rows - 1 - step;
            if (row < 0 || row >= _rows) continue;
            while (blocked < 24)
            {
                int k = _cursor[row];
                if (k >= _cols) break;
                _cursor[row] = k + 1;
                int col = (k & 1) == 0 ? k / 2 : -(k + 1) / 2;   // 0, -1, 1, -2, 2 ...: the middle outwards
                double lateral = col * _pitch + (_rng.NextDouble() * 2 - 1) * GridJitter * _pitch;
                double along = row * _pitch + (_rng.NextDouble() * 2 - 1) * GridJitter * _pitch * (row == 0 ? 0 : 1);
                var (x, y) = At(Math.Max(0, along), lateral);
                if (TryCell(x, y, body, walk, out var at)) return at;
                blocked++;   // the slot is lost (the cursor moved past it): the next slot of the row
            }
        }
        return null;
    }

    /// <summary>The cell itself or one of the cells next to it, when walkable and clear of the members already placed.</summary>
    bool TryCell(int x, int y, int body, int walk, out (int X, int Y) at)
    {
        if (Open(x, y, walk) && !Crowded(x, y, body)) { Remember(x, y, body); at = (x, y); return true; }
        foreach (var (dx, dy) in Nearby)
            if (Open(x + dx, y + dy, walk) && !Crowded(x + dx, y + dy, body)) { Remember(x + dx, y + dy, body); at = (x + dx, y + dy); return true; }
        at = default;
        return false;
    }

    /// <summary>A loose group: random spots in the band's rectangle, each at least the two bodies' spacing from every member placed.</summary>
    (int X, int Y)? PlaceLoose(int from, int to, int body, int walk, bool centred)
    {
        // the band first; when it is full, the whole block (the rear half for the general)
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < Tries; i++)
            {
                double lo = pass == 0 ? from : centred ? 50 : 0, hi = pass == 0 ? to : 100;
                double factor = LooseFactorMin + _rng.NextDouble() * (LooseFactorMax - LooseFactorMin);
                double pct = lo + _rng.NextDouble() * Math.Max(0, hi - lo);
                double along = Math.Clamp(pct, 0, 100) / 100 * _depth;
                bool middle = centred && pass == 0;
                double lateral = middle ? (_rng.NextDouble() * 2 - 1) * GeneralSpread * _pitch : (_rng.NextDouble() - 0.5) * _width;
                if (middle) along = _depth;
                var (x, y) = At(along, lateral);
                if (!Open(x, y, walk) || Crowded(x, y, body, factor)) continue;
                Remember(x, y, body);
                return (x, y);
            }
        return null;
    }

    (int, int) CellOf(int x, int y) => ((int)Math.Floor(x / _hashCell), (int)Math.Floor(y / _hashCell));

    bool Crowded(int x, int y, int body, double factor = 1)
    {
        var (gx, gy) = CellOf(x, y);
        for (int i = gx - 1; i <= gx + 1; i++)
            for (int j = gy - 1; j <= gy + 1; j++)
                if (_placed.TryGetValue((i, j), out var list))
                    foreach (var p in list)
                    {
                        double min = _orderly ? _pitch * GridOverlapFraction : factor * Math.Max(LooseSpacingMin, (body + p.Body) / 2.0 * LooseSpacingPerBody);
                        double dx = p.X - x, dy = p.Y - y;
                        if (dx * dx + dy * dy < min * min) return true;
                    }
        return false;
    }

    void Remember(int x, int y, int body)
    {
        var cell = CellOf(x, y);
        if (!_placed.TryGetValue(cell, out var list)) _placed[cell] = list = [];
        list.Add((x, y, body));
    }
}
