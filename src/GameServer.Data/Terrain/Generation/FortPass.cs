namespace GameServer.Data.Terrain.Generation;

/// <summary>The late feature pass (<c>0x142035340</c>, after every subgraph block and before the outer buffers): every
/// extra feature row with the +0x61 flag and exactly one room (the Betrayal forts) that the fill-area pass left unplaced
/// is set on an island carved out of the water beside a straight run of a shore edge (<c>0x142032970</c> per feature).
/// The doors (<c>0x142031f60</c>) are the tile-origin cells of the primary record's inner rectangle whose untagged
/// square tile has exactly two walkable corner grounds and, turned by its orientation, exactly two edges, the same real
/// type on opposite sides (<c>0x142030fe0</c>: no height step, exactly one walkable ground, no split edges, no inside
/// ground, and the store holding its corner tiles both ways round and its straight tile), of the tile's size, with
/// square untagged tiles of that size one tile away along the edge; the door's side is the free side whose two corners
/// are not both walkable. The candidates (<c>0x142031a30</c>) are rects of the feature's size rounded up to the type's
/// unit plus one unit each side, laid along the outside of each door by its side, kept when every cell lies inside the
/// inner rectangle on untagged, edgeless tiles whose four corner grounds are the type's non-walkable one
/// (<c>0x14203bd30</c>); <c>Next(count)</c> (unused) then the hidden shuffle. The first candidate that works places the
/// fort: the twelve store keys of the type's grounds must all have tiles; the tiles overhanging the rect are noted, every
/// tile covering the rect removed, the interior vertices cleared, the fort's and the door's vertices re-grounded with a
/// walkable ground among the door tile's corners, and the ring re-tiled by weighted picks (the door cell and the cell
/// across it, the two corner pieces beside them, two chisels on the door's axis (<c>0x142034f10</c>), the rect's corners,
/// its edge runs, the 1-cell inner ring), the overhang refilled with the other ground's plain tile, null vertices
/// re-grounded, and the fort tiled by the fill-room routine (<c>0x141fef200</c>). Verified on the server corpus's
/// Promenade 3238478791, the only load with the pass.</summary>
sealed class FortPass(TerrainCache files, LevelRandom rng, List<string> notes, TileGrid grid, TileStore store,
    Dictionary<string, List<(int Weight, string Path, int Orientation)>> fills, Fill fill, (int X0, int Y0, int X1, int Y1) inner,
    IReadOnlyList<(int X0, int Y0, int X1, int Y1)> named, (int X, int Y) viewOrigin)
{
    static readonly int[] DX = [0, 1, 0, -1], DY = [-1, 0, 1, 0];

    int Flags(string? g) => g is null ? 0 : files.Ground(g)?.Flags ?? 0;

    /// <summary>A ground with .gt bits 2 and 3; a null ground counts as walkable (<c>0x142030fe0</c> / <c>0x142031160</c>).</summary>
    bool Walkable(string? g) => g is null || ((Flags(g) & 4) != 0 && (Flags(g) & 8) != 0);

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    readonly Dictionary<string, bool> _doorOk = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>0x142030fe0</c>: a real edge type without a height step, exactly one of its two grounds walkable, not
    /// virtual, no inside ground, and the store holds its corner tile (<c>0x1420306e0</c>), the corner with the grounds
    /// swapped (<c>0x1420309e0</c>) and its straight tile (<c>0x142030ce0</c>), all untagged.</summary>
    bool DoorTypeOk(string? path)
    {
        if (path is null) return false;
        if (_doorOk.TryGetValue(path, out bool known)) return known;
        var et = files.Edge(path);
        bool ok = false;
        if (et is not null && et.N0 == 0 && (Walkable(et.Ground1) ? 1 : 0) + (Walkable(et.Ground2) ? 1 : 0) == 1 && !et.Virtual && et.CentreGround is null)
        {
            int u = et.Size;
            string? a = et.Ground1, b = et.Ground2;
            ok = store.Candidates("", BuildKey(u, path, [0, 1], new() { [1] = b }, a)).Count != 0
                && store.Candidates("", BuildKey(u, path, [0, 1], new() { [1] = a }, b)).Count != 0
                && store.Candidates("", BuildKey(u, path, [0, 2], new() { [1] = b, [2] = b }, a)).Count != 0;
        }
        _doorOk[path] = ok;
        return ok;
    }

    /// <summary>(walkable ground, other ground) of the type: the first ground when it is null or walkable, else the second.</summary>
    (string? Walk, string? Other) TypeGrounds(EdgeType et) =>
        et.Ground1 is null || Walkable(et.Ground1) ? (et.Ground1, et.Ground2) : (et.Ground2, et.Ground1);

    static TileKey BuildKey(int unit, string et, int[] typeSides, Dictionary<int, string?> special, string? defaultGround)
    {
        var k = new TileKey { Width = (byte)unit, Height = (byte)unit };
        for (int i = 0; i < 4; i++) { k.PointA[i] = (byte)(3 * unit); k.PointB[i] = (byte)(3 * unit); }
        foreach (int s in typeSides)
        {
            k.Edges[s] = et;
            k.PointA[s] = (byte)((3 * unit) >> 1);
        }
        for (int j = 0; j < 4; j++) k.Grounds[j] = special.TryGetValue(j, out var g) ? g : defaultGround;
        return k;
    }

    /// <summary><c>0x142032190</c>(side, A, B): a unit × unit key with the type on sides `side` and `side + 1`, the connection
    /// points centred there, corner A at `side + 1` and B at the three others.</summary>
    static (int[] Sides, Dictionary<int, string?> Special, string? Default) CornerKey(int side, string? a, string? b) =>
        ([side, (side + 1) & 3], new() { [(side + 1) & 3] = a }, b);

    /// <summary>The twelve lists: per side the corner keys walkable/other and other/walkable, and the edge-run key
    /// (<c>0x1420324b0</c>: the type on `side` and `side − 2`, A at side + 1 and side − 2, B at the other two).</summary>
    (List<(TileDef, int, int)>[] Cw, List<(TileDef, int, int)>[] Co, List<(TileDef, int, int)>[] Ed) KeyLists(int unit, string et, string? walk, string? other)
    {
        var cw = new List<(TileDef, int, int)>[4];
        var co = new List<(TileDef, int, int)>[4];
        var ed = new List<(TileDef, int, int)>[4];
        for (int s = 0; s < 4; s++)
        {
            var (ts, sp, df) = CornerKey(s, walk, other);
            cw[s] = store.Candidates("", BuildKey(unit, et, ts, sp, df));
            (ts, sp, df) = CornerKey(s, other, walk);
            co[s] = store.Candidates("", BuildKey(unit, et, ts, sp, df));
            ed[s] = store.Candidates("", BuildKey(unit, et, [s, (s - 2) & 3], new() { [(s + 1) & 3] = walk, [(s - 2) & 3] = walk }, other));
        }
        return (cw, co, ed);
    }

    /// <summary><c>0x1420327d0</c>(ground, size): no edges, four corners = the ground, points 3 (the raw bytes 0x03030303).</summary>
    static TileKey GroundKey(string? ground, int size)
    {
        var k = new TileKey { Width = (byte)size, Height = (byte)size };
        for (int i = 0; i < 4; i++) { k.Grounds[i] = ground; k.PointA[i] = 3; k.PointB[i] = 3; }
        return k;
    }

    /// <summary><c>0x142031160</c> for the tile whose origin is (x, y): (type path, side, height) or null.</summary>
    (string Type, int Side, short Height)? DoorEntry(TileEntry e, int x, int y)
    {
        if (!e.IsOrigin) return null;
        var t = e.Tile;
        if (t.Tag.Length != 0 || x == inner.X0 || y == inner.Y0) return null;
        if (t.Width != t.Height) return null;
        if (t.Key.Grounds.Count(Walkable) != 2) return null;
        if (x + t.Width >= inner.X1 || y + t.Height >= inner.Y1) return null;
        var rk = t.Key.Rotate(e.Orientation);
        var passing = new List<(string Path, int Side)>();
        int nonNull = 0;
        for (int s = 0; s < 4; s++)
        {
            if (rk.Edges[s] is not null) nonNull++;
            if (DoorTypeOk(rk.Edges[s])) passing.Add((rk.Edges[s]!, s));
        }
        if (nonNull != 2 || passing.Count != 2 || !Same(passing[0].Path, passing[1].Path)) return null;
        var et = files.Edge(passing[0].Path)!;
        if (et.Size != t.Width) return null;
        int s1 = passing[0].Side, s2 = passing[1].Side;
        if (((s1 - 2) & 3) != s2) return null;
        int lo = Enumerable.Range(0, 4).First(v => v != s1 && v != s2);
        int hi = Enumerable.Range(0, 4).Reverse().First(v => v != s1 && v != s2);
        // The free side whose two corners are walkable is the land side: the fort goes across the other one.
        int side = Walkable(rk.Grounds[lo]) && Walkable(rk.Grounds[(lo + 1) & 3]) ? hi : lo;
        // The neighbours one tile away along the edge line.
        foreach (int s in new[] { s1, s2 })
        {
            int nx = x + DX[s] * t.Width, ny = y + DY[s] * t.Width;
            if (!grid.Inside(nx, ny) || grid[nx, ny] is not { } c2) return null;
            var t2 = c2.Tile;
            if (t2.Width != t2.Height || t2.Width != et.Size || t2.Tag.Length != 0) return null;
        }
        return (passing[0].Path, side, e.HeightOffset);
    }

    /// <summary><c>0x142031f60</c>: the straight-edge tiles of the inner rectangle, then in (x, y) order (the door map
    /// <c>0x1420369e0</c> is keyed by position, and the candidates are built in that order).</summary>
    List<(int X, int Y, string Type, int Side, short Height)> DoorList()
    {
        var result = new List<(int, int, string, int, short)>();
        for (int i = inner.Y0; i < inner.Y1; i++)
        {
            int j = inner.X0;
            while (j < inner.X1)
            {
                if (grid[j, i] is { IsOrigin: true } c && c.Tile.Tag.Length == 0)
                {
                    var (rw, rh) = Orientation.Size(c.Orientation, c.Tile.Width, c.Tile.Height);
                    bool hit = named.Any(r => !(j >= r.X1 || j + rw <= r.X0 || i >= r.Y1 || i + rh <= r.Y0));
                    if (!hit)
                    {
                        if (DoorEntry(c, j, i) is { } e) result.Add((j, i, e.Type, e.Side, e.Height));
                        j += rw - 1;
                    }
                }
                j++;
            }
        }
        return result.OrderBy(d => d.Item1).ThenBy(d => d.Item2).ToList();
    }

    /// <summary><c>0x14203bd30</c>: every cell of the rect inside the inner rectangle; a tiled cell's tile untagged, without a
    /// real edge (a wildcard one passes) and with all four raw corner grounds equal to the type's non-walkable ground.</summary>
    bool RectOnWater((int X0, int Y0, int X1, int Y1) rect, string? water)
    {
        for (int y = rect.Y0; y < rect.Y1; y++)
            for (int x = rect.X0; x < rect.X1; x++)
            {
                if (x < inner.X0 || x >= inner.X1 || y < inner.Y0 || y >= inner.Y1) return false;
                if (grid[x, y] is not { } c) continue;
                var t = c.Tile;
                if (t.Tag.Length != 0) return false;
                if (t.Key.Edges.Any(e => e is not null && files.Edge(e)?.Wildcard != true)) return false;
                if (t.KeyGrounds.Any(g => !Same(g, water))) return false;
            }
        return true;
    }

    /// <summary><c>0x142031a30</c>: the rects beside each door, then <c>Next(count)</c> and the hidden shuffle.</summary>
    List<((int X, int Y) Door, (int X0, int Y0, int X1, int Y1) Rect, int Side, short Height, string Type)> Candidates(
        List<(int X, int Y, string Type, int Side, short Height)> doors, int fw, int fh)
    {
        var result = new List<((int, int), (int, int, int, int), int, short, string)>();
        foreach (var (x, y, etPath, side, h) in doors)
        {
            var et = files.Edge(etPath)!;
            int unit = et.Size;
            string? water = TypeGrounds(et).Other;
            int rh = 2 * unit + fh + (fh % unit != 0 ? unit - fh % unit : 0);
            int rw = 2 * unit + fw + (fw % unit != 0 ? unit - fw % unit : 0);
            void Push(int rx, int ry)
            {
                var r = (rx, ry, rx + rw, ry + rh);
                if (RectOnWater(r, water)) result.Add(((x, y), r, side, h, etPath));
            }
            switch (side)
            {
                case 1: for (int i = 2 * unit + y - rh; i < y; i += unit) Push(x + unit, i); break;
                case 2: for (int j = 2 * unit + x - rw; j < x; j += unit) Push(j, y + unit); break;
                case 3: for (int i = 2 * unit + y - rh; i < y; i += unit) Push(x - rw, i); break;
                default: for (int j = 2 * unit + x - rw; j < x; j += unit) Push(j, y - rh); break;
            }
        }
        rng.Next((uint)result.Count);
        rng.Shuffle(result);
        return result;
    }

    /// <summary>Runs the pass for one row: its single room placed on the first candidate that works.</summary>
    public bool Place(ExtraFeatureRow row)
    {
        string arm = row.Rooms[0];
        var room = files.Room(arm) ?? throw new FileNotFoundException(arm);
        int fw = room.Width, fh = room.Height;
        var doors = DoorList();
        var cands = Candidates(doors, fw, fh);
        notes.Add($"late feature pass {row.Name}: {doors.Count} doors, {cands.Count} candidate rects");
        bool check = grid.CheckGrounds;
        grid.CheckGrounds = false;
        try
        {
            foreach (var (door, rect, side, height, et) in cands)
                if (TryCandidate(row, room, fw, fh, door, rect, side, height, et)) return true;
        }
        finally { grid.CheckGrounds = check; }
        return false;
    }

    (TileDef Tile, int Orientation) Pick(List<(TileDef Tile, int Orientation, int Weight)> list)
    {
        uint r = rng.Next((uint)list.Sum(c => c.Weight));
        foreach (var c in list)
        {
            if (r < (uint)c.Weight) return (c.Tile, c.Orientation);
            r -= (uint)c.Weight;
        }
        return (list[^1].Tile, list[^1].Orientation);
    }

    bool Free(int x, int y, int w, int h)
    {
        for (int v = 0; v < h; v++)
            for (int u = 0; u < w; u++)
                if (!grid.Inside(x + u, y + v) || grid[x + u, y + v] is not null) return false;
        return true;
    }

    /// <summary>The first tile of a ground's fill group (<c>0x14202b8d0</c> → <c>0x141ff67d0</c>), as the outer buffer takes it.</summary>
    (TileDef Tile, int Orientation)? FirstFill(string? ground)
    {
        string name = ground is null ? "default" : files.Ground(ground)?.Name ?? "default";
        if ((!fills.TryGetValue(name, out var list) || list.Count == 0) && (!fills.TryGetValue("default", out list) || list.Count == 0)) return null;
        var (_, path, o) = list[0];
        if (path.EndsWith(".arm", StringComparison.OrdinalIgnoreCase)) return null;
        return files.Tile(path) is { } t ? (t, o == 8 ? 0 : o) : null;
    }

    bool TryCandidate(ExtraFeatureRow row, RoomDef room, int fw, int fh, (int X, int Y) door, (int X0, int Y0, int X1, int Y1) rect, int side, short height, string etPath)
    {
        var (dx, dy) = door;
        var (rx0, ry0, rx1, ry1) = rect;
        var et = files.Edge(etPath)!;
        int unit = et.Size;
        int fy = (ry1 - ry0 - 2 * unit - fh) / 2 + unit + ry0;
        int fx = unit + rx0 + (rx1 - 2 * unit - rx0 - fw) / 2;
        foreach (var n in named)
            if (!(fx >= n.X1 || fx + fw <= n.X0 || fy >= n.Y1 || fy + fh <= n.Y0)) return false;
        var (walk, other) = TypeGrounds(et);
        var (cw, co, ed) = KeyLists(unit, etPath, walk, other);
        var ground = store.Candidates("", GroundKey(walk, 1));
        if (ground.Count == 0) ground = store.Candidates("", GroundKey(null, 1));
        if (ground.Count == 0 || cw.Any(l => l.Count == 0) || co.Any(l => l.Count == 0) || ed.Any(l => l.Count == 0)) return false;
        // The overhang: cells of tiles covering the rect that lie outside it.
        var overhang = new List<(int X, int Y)>();
        for (int y = ry0; y < ry1; y++)
            for (int x = rx0; x < rx1; x++)
            {
                if (grid[x, y] is not { } c) continue;
                int ox = x - c.OffsetX, oy = y - c.OffsetY;
                var (w, h) = Orientation.Size(c.Orientation, c.Tile.Width, c.Tile.Height);
                if (ox < rx0 || oy < ry0 || ox + w > rx1 || oy + h > ry1)
                    for (int yy = oy; yy < oy + h; yy++)
                        for (int xx = ox; xx < ox + w; xx++)
                            if (xx < rx0 || xx >= rx1 || yy < ry0 || yy >= ry1) overhang.Add((xx, yy));
            }
        (TileDef Tile, int Orientation)? overTile = null;
        if (overhang.Count != 0)
        {
            var list = store.Candidates("", GroundKey(other, unit));
            if (list.Count == 0 && unit > 1) list = store.Candidates("", GroundKey(other, 1));
            if (list.Count != 0) overTile = Pick(list);
            else
            {
                overTile = FirstFill(other);
                if (overTile is null) return false;
            }
        }
        // The re-ground ground: a walkable ground among the door tile's corners that differs from the type's.
        string? reground = walk;
        for (int y = dy; y <= dy + unit && reground == walk; y++)
            for (int x = dx; x <= dx + unit; x++)
            {
                string? g = grid.InsideCorners(x, y) ? grid.Corner(x, y).Ground : null;
                if (g is not null && Walkable(g) && !Same(g, walk)) { reground = g; break; }
            }
        // Clear every tile covering the rect, the interior vertices, re-ground the fort's and the door's vertices.
        for (int y = ry0; y < ry1; y++)
            for (int x = rx0; x < rx1; x++)
                if (grid[x, y] is not null) grid.Remove(x, y);
        for (int y = ry0 + 1; y < ry1; y++)
            for (int x = rx0 + 1; x < rx1; x++)
                if (grid.InsideCorners(x, y))
                {
                    ref var c = ref grid.Corner(x, y);
                    c.Ground = null;
                    c.Flags &= unchecked((byte)~1);
                }
        for (int y = fy; y <= fy + fh; y++)
            for (int x = fx; x <= fx + fw; x++)
                if (grid.InsideCorners(x, y)) grid.ForceGround(x, y, reground);
        for (int y = dy; y <= dy + unit; y++)
            for (int x = dx; x <= dx + unit; x++)
                if (grid.InsideCorners(x, y)) grid.ForceGround(x, y, reground);
        grid.Remove(dx, dy);

        bool PlacePick(List<(TileDef, int, int)> list, int x, int y)
        {
            var (tile, o) = Pick(list);
            var (w, h) = Orientation.Size(o, tile.Width, tile.Height);
            if (!Free(x, y, w, h)) return false;
            grid.Place(tile, o, x, y, height);
            return true;
        }
        void Ring(int x0, int y0)
        {
            for (int y = y0; y < y0 + unit; y++)
                for (int x = x0; x < x0 + unit; x++) PlacePick(ground, x, y);
        }
        Ring(dx, dy);
        switch (side)
        {
            case 3:
                Ring(dx - unit, dy);
                PlacePick(dy + 2 * unit < ry1 ? co[1] : ed[3], dx - unit, dy + unit);
                PlacePick(dy - unit > ry0 ? co[0] : ed[1], dx - unit, dy - unit);
                Chisel(dx, dy + unit, 0, false, (dx, dy - unit), height);
                Chisel(dx, dy - unit, 2, true, null, height);
                break;
            case 2:
                Ring(dx, dy + unit);
                PlacePick(dx - unit > rx0 ? co[3] : ed[0], dx - unit, dy + unit);
                PlacePick(dx + 2 * unit < rx1 ? co[0] : ed[2], dx + unit, dy + unit);
                Chisel(dx + unit, dy, 3, false, (dx - unit, dy), height);
                Chisel(dx - unit, dy, 1, true, null, height);
                break;
            case 1:
                Ring(dx + unit, dy);
                PlacePick(dy + 2 * unit < ry1 ? co[2] : ed[3], dx + unit, dy + unit);
                PlacePick(dy - unit > ry0 ? co[3] : ed[1], dx + unit, dy - unit);
                Chisel(dx, dy + unit, 0, true, (dx, dy - unit), height);
                Chisel(dx, dy - unit, 2, false, null, height);
                break;
            default:
                Ring(dx, dy - unit);
                PlacePick(dx - unit > rx0 ? co[2] : ed[0], dx - unit, dy - unit);
                PlacePick(dx + 2 * unit < rx1 ? co[1] : ed[2], dx + unit, dy - unit);
                Chisel(dx + unit, dy, 3, true, (dx - unit, dy), height);
                Chisel(dx - unit, dy, 1, false, null, height);
                break;
        }
        // The rect's corners, then its edge runs, then the 1-cell inner ring outside the fort.
        PlacePick(cw[0], rx0, ry1 - unit);
        PlacePick(cw[1], rx0, ry0);
        PlacePick(cw[2], rx1 - unit, ry0);
        PlacePick(cw[3], rx1 - unit, ry1 - unit);
        for (int j = rx0 + unit; j < rx1 - unit; j += unit)
        {
            PlacePick(ed[1], j, ry0);
            PlacePick(ed[3], j, ry1 - unit);
        }
        for (int i = ry0 + unit; i < ry1 - unit; i += unit)
        {
            PlacePick(ed[0], rx0, i);
            PlacePick(ed[2], rx1 - unit, i);
        }
        for (int y = ry0 + unit; y < ry1 - unit; y++)
            for (int x = rx0 + unit; x < rx1 - unit; x++)
                if (x < fx || x >= fx + fw || y < fy || y >= fy + fh) PlacePick(ground, x, y);
        if (overTile is { } ot)
        {
            var (w, h) = Orientation.Size(ot.Orientation, ot.Tile.Width, ot.Tile.Height);
            foreach (var (x, y) in overhang)
                if (Free(x, y, w, h)) grid.Place(ot.Tile, ot.Orientation, x, y, height);
        }
        // The vertices still without a ground take the re-ground: the rect's [x0, x1) × [y0, y1) and the door's
        // [dx, dx + unit) × [dy, dy + unit), both half-open (the earlier unconditional loops ran to dx + unit and
        // fx + fw inclusive): a corner on the door square's far edge a chisel nulled stays null.
        for (int y = ry0; y < ry1; y++)
            for (int x = rx0; x < rx1; x++)
                if (grid.InsideCorners(x, y) && grid.Corner(x, y).Ground is null) grid.ForceGround(x, y, reground);
        for (int y = dy; y < dy + unit; y++)
            for (int x = dx; x < dx + unit; x++)
                if (grid.InsideCorners(x, y) && grid.Corner(x, y).Ground is null) grid.ForceGround(x, y, reground);
        // The fort itself: the fill-room routine at the centre, orientation 0, the door's height, no ground.
        fill.PlaceRoom(room, fx - viewOrigin.X, fy - viewOrigin.Y, 0, height, null, false);
        notes.Add($"late feature pass: {row.Name} on the island at ({fx}, {fy}) {fw}x{fh} beside the door ({dx}, {dy}) side {side} (rect ({rx0}, {ry0})-({rx1}, {ry1}))");
        return true;
    }

    /// <summary><c>0x142034f10</c>(x, y, side, flip, other): the untagged tile at (x, y) gets its key turned by its orientation,
    /// the edge of `side` moved to `side − 2(flip ^ 1) + 1` (its connection centred) and cleared on `side`, the corner
    /// across that side copied (flip: side + 1 → the moved side; else side → side + 1); the store lookup (then with the
    /// flag-0x10 corners cleared; with nothing both cells are cleared and the client throws), <c>Next(total)</c>, placed
    /// with the cell's height and the unconditional corner setter (the place call's last argument is 1 here, 0 in
    /// every other placement of the pass): a corner the tile has no ground for loses its ground. The Promenade's
    /// chiselled shore tile nulls the corner (48, 84), so the doodad stage drops an attempt there as the client does.</summary>
    void Chisel(int x, int y, int side, bool flip, (int X, int Y)? other, short height)
    {
        if (grid[x, y] is not { } c) return;
        var t = c.Tile;
        if (t.Tag.Length != 0) return;
        var k = t.Key.Rotate(c.Orientation);
        int moved = (side - 2 * (flip ? 0 : 1) + 1) & 3;
        k.Edges[moved] = k.Edges[side];
        k.PointA[moved] = (byte)((3 * t.Width) >> 1);
        k.Edges[side] = null;
        k.PointA[side] = (byte)(3 * t.Width);
        for (int i = 0; i < 4; i++) k.PointB[i] = (byte)(3 * t.Width);
        if (flip) k.Grounds[moved] = k.Grounds[side];
        else k.Grounds[side] = k.Grounds[(side + 1) & 3];
        var list = store.Candidates("", k);
        if (list.Count == 0)
        {
            var k2 = k.Clone();
            for (int i = 0; i < 4; i++)
                if (k2.Grounds[i] is { } g && (Flags(g) & 0x10) != 0) k2.Grounds[i] = null;
            list = store.Candidates("", k2);
            if (list.Count == 0)
            {
                grid.Remove(x, y);
                if (other is { } o && grid[o.X, o.Y] is not null) grid.Remove(o.X, o.Y);
                notes.Add($"late feature pass: chisel found no tile key at ({x}, {y}) (the client throws)");
                return;
            }
        }
        var (tile, orientation) = Pick(list);
        grid.Remove(x, y);
        grid.Place(tile, orientation, x, y, c.HeightOffset, force: true);
    }
}
