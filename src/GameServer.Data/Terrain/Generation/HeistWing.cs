namespace GameServer.Data.Terrain.Generation;

/// <summary>The HeistPlugin's hooks in the dungeon pipeline: the wing block (<c>0x141f47f10</c>: one placed room per wing
/// room, picked from the wing master's room set by the room's type name and its door ring with the logged draw at
/// <c>0x141f48756</c>) and the door pass after every block (<c>0x141f492f0</c>: each wing door's tile re-picked from the
/// door type's tile group, then the HeistDoors side tiles beside it, <c>0x141f49a30</c>).</summary>
static class HeistWing
{
    public const string PathEt = "Metadata/Terrain/Leagues/Heist/PathRooms.et";
    public const string PathInEt = "Metadata/Terrain/Leagues/Heist/PathRoomsIn.et";
    public const string PathOutEt = "Metadata/Terrain/Leagues/Heist/PathRoomsOut.et";

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>0x141f48a30</c>: the 2(w + h) exit ring of wing room k: PathRooms.et at every door on its sides, PathRoomsIn /
    /// PathRoomsOut when the room has a hazard and both rooms of the door are path rooms (index &lt; nPath); the ring order
    /// is the record's exit order (north left to right, east top to bottom, south right to left, west bottom to top).</summary>
    static string?[] RoomKey(HeistWingLayout wing, int k, int nPath)
    {
        var r = wing.Rooms[k];
        int w = r.W, h = r.H;
        var ring = new string?[2 * (w + h)];
        bool hazard = r.Row is { } row && row.Hazard && row.Type <= 2;
        foreach (var d in wing.Doors)
        {
            bool bothPath = d.Ia < nPath && d.Ib < nPath;
            bool flag = hazard && bothPath;
            int x, y, dx, dy;
            string et;
            if (r.X <= d.Ax && d.Ax < r.X + w && r.Y <= d.Ay && d.Ay < r.Y + h)
            {
                (x, y, dx, dy) = (d.Ax - r.X, d.Ay - r.Y, d.Bx - d.Ax, d.By - d.Ay);
                et = flag ? PathOutEt : PathEt;
            }
            else if (r.X <= d.Bx && d.Bx < r.X + w && r.Y <= d.By && d.By < r.Y + h)
            {
                (x, y, dx, dy) = (d.Bx - r.X, d.By - r.Y, d.Ax - d.Bx, d.Ay - d.By);
                et = flag ? PathInEt : PathEt;
            }
            else continue;
            // 0x141f48ba0's side code from the delta.
            int idx;
            if (dx > 0 && dx > Math.Abs(dy)) idx = w + y;
            else if (dx < 0 && -dx > Math.Abs(dy)) idx = 2 * (w + h) - y - 1;
            else if (dy > 0) idx = 2 * w + h - x - 1;
            else idx = x;
            ring[idx] = et;
        }
        return ring;
    }

    /// <summary><c>0x141fff2a0</c>(room set, name, key, mode): the set's rooms tagged <paramref name="name"/> in every allowed
    /// orientation (set order) of the key's size whose turned exits match the ring: mode 0 every slot equal (a null slot
    /// needs no exit), mode 2 the ring's non-null slots equal.</summary>
    static List<(DungeonRoom Room, int O, int W, int H, int Weight)> KeyLookup(DungeonRoomSet set, string name, int w, int h, string?[] ring, int mode, Func<string, bool> directional)
    {
        var result = new List<(DungeonRoom, int, int, int, int)>();
        foreach (var (room, o, rw, rh, exits) in set.Variants(name, directional))
        {
            if ((rw, rh) != (w, h)) continue;
            bool ok = true;
            for (int i = 0; i < 2 * (w + h); i++)
            {
                var want = ring[i];
                var d = exits[i];
                if (want is null)
                {
                    if (mode == 0 && d.Type is not null) { ok = false; break; }
                }
                else if (!Same(d.Type, want) || d.Height != 0 || d.Chirality) { ok = false; break; }
            }
            if (ok && room.Weight != 0) result.Add((room, o, rw, rh, room.Weight));
        }
        return result;
    }

    /// <summary>Per wing room in order: the strict pass, else mode 2 (<c>0x141f48d20</c>), else the hazard fallbacks of
    /// <c>0x141f47f10</c> (the In/Out-only key, then the all-PathRooms key).</summary>
    static List<(string Name, string?[] Ring, List<(DungeonRoom Room, int O, int W, int H, int Weight)> Cands, string How)> Candidates(
        HeistWingLayout wing, DungeonRoomSet set, TerrainCache files, Func<string, bool> directional)
    {
        int nPath = wing.Rooms.FindIndex(r => r.Row is { Type: > 2 });
        if (nPath < 0) nPath = wing.Rooms.Count;
        var result = new List<(string, string?[], List<(DungeonRoom, int, int, int, int)>, string)>();
        for (int k = 0; k < wing.Rooms.Count; k++)
        {
            var r = wing.Rooms[k];
            var ring = RoomKey(wing, k, nPath);
            string name = r.Row is { } row ? (files.Room(row.Arm) ?? throw new FileNotFoundException(row.Arm)).Tag : "";
            var cands = KeyLookup(set, name, r.W, r.H, ring, 0, directional);
            if (cands.Count == 0) cands = KeyLookup(set, name, r.W, r.H, ring, 2, directional);
            string how = "key";
            bool hazard = r.Row is { } hr && hr.Hazard && hr.Type <= 2;
            if (cands.Count == 0 && k <= nPath && hazard)
            {
                var ring2 = ring.Select(e => Same(e, PathInEt) || Same(e, PathOutEt) ? e : null).ToArray();
                cands = KeyLookup(set, name, r.W, r.H, ring2, 0, directional);
                if (cands.Count == 0) cands = KeyLookup(set, name, r.W, r.H, ring2, 2, directional);
                how = "in/out key";
                if (cands.Count == 0)
                {
                    var ring3 = ring.Select(e => e is null ? null : PathEt).ToArray();
                    cands = KeyLookup(set, name, r.W, r.H, ring3, 0, directional);
                    if (cands.Count == 0) cands = KeyLookup(set, name, r.W, r.H, ring3, 2, directional);
                    how = "plain key";
                }
            }
            result.Add((name, ring, cands, how));
        }
        return result;
    }

    /// <summary><c>0x141f47f10</c>: one placed room per wing room, the LOGGED draw(total weight) at <c>0x141f48756</c>, the weighted
    /// pick (<c>0x141f56260</c>), <c>0x142105080</c> at the room's unit position with the PathRooms.et door type; then the
    /// entry's exits are REPLACED by the room's door ring (<c>0x141f47f10</c>+0x9b0 after <c>0x142105080</c>), so a room
    /// admitted in mode 2 with more typed exits than the ring opens no extra door.</summary>
    public static void PlaceRooms(DungeonMap map, DungeonRoomSet set, HeistWingLayout wing, TerrainCache files, LevelRandom rng, List<string> notes)
    {
        var directional = map.Directional;
        var all = Candidates(wing, set, files, directional);
        for (int k = 0; k < all.Count; k++)
        {
            var (name, ring, cands, how) = all[k];
            var r = wing.Rooms[k];
            if (cands.Count == 0) throw new InvalidOperationException($"No matching rooms found at room ({r.X}, {r.Y}) '{name}' ({how})");
            int total = cands.Sum(c => c.Weight);
            int pick = (int)rng.Next((uint)total);
            var chosen = cands[^1];
            foreach (var c in cands)
            {
                if (pick < c.Weight) { chosen = c; break; }
                pick -= c.Weight;
            }
            var p = map.Place(chosen.Room, chosen.O, chosen.W, chosen.H, r.X, r.Y, 0, [], PathEt, true, false, 1, 0, null);
            for (int i = 0; i < p.Exits.Length; i++) p.Exits[i] = new DungeonDoor { Type = ring[i], Height = 0, Chirality = false };
            notes.Add($"  heist room {k}: {Path.GetFileName(chosen.Room.Path)} o{chosen.O} at ({r.X}, {r.Y}) {chosen.W}x{chosen.H} of {cands.Count} ({total}) by {how}");
        }
    }

    /// <summary>A wing's record on the tile map: its layout, the view origin (the record's corner inside its margins),
    /// the master's unit and overlap and its tile store.</summary>
    public sealed record WingRecord(HeistWingLayout Wing, int ViewX, int ViewY, int Unit, int Overlap, TileStore Store);

    // dword_142fbf730: the direction code's unit vector.
    static readonly (int X, int Y)[] Dirs = [(0, -1), (1, 0), (0, 1), (-1, 0)];

    /// <summary><c>0x141f492f0</c>'s direction code of a wing door from (A − B): 0 north, 1 east, 2 south, 3 west.</summary>
    static int DoorDirection(HeistDoor d)
    {
        int dx = d.Ax - d.Bx, dy = d.Ay - d.By;
        if (dx > 0 && dx > Math.Abs(dy)) return 1;
        if (dx < 0 && -dx > Math.Abs(dy)) return 3;
        return dy > 0 ? 2 : 0;
    }

    static (TileDef Tile, int Orientation) Pick(List<(TileDef Tile, int Orientation, int Weight)> cands, uint r)
    {
        foreach (var c in cands)
        {
            if (r < (uint)c.Weight) return (c.Tile, c.Orientation);
            r -= (uint)c.Weight;
        }
        return (cands[^1].Tile, cands[^1].Orientation);
    }

    /// <summary><c>0x141f492f0</c> after every block: each wing door's tile (the door stage's "door" tile at the unit centre
    /// between the two rooms, a 1 × 3) is re-picked from the door type's tile group by the tile's own key turned by its
    /// orientation: draw(total weight) at <c>0x141f4960d</c>, placed with the door's direction as orientation (turned
    /// round for an "out" type); then per side-tile tag of the HeistDoors row (+56: the 5-cell line one cell on the door's
    /// +dir side; +72: the −dir side) the first origin cell whose tile has no tag of its own and whose turned key has
    /// candidates under the tag is re-picked (draw at <c>0x141f49c34</c>, the pick's orientation, the cell's height).</summary>
    public static void DoorPass(HeistLevel level, IReadOnlyList<WingRecord> records, TileGrid grid, LevelRandom rng, List<string> notes)
    {
        foreach (var wing in level.Wings)
        {
            var rec = records.FirstOrDefault(r => r.Wing == wing);
            if (rec is null) { notes.Add("heist door pass: a wing without a record"); continue; }
            int u = rec.Unit - rec.Overlap, half = rec.Unit / 2;
            foreach (var d in wing.Doors)
            {
                int cx = (u * (d.Ax + d.Bx) + 2 * (half + rec.ViewX)) / 2;
                int cy = (u * (d.Ay + d.By) + 2 * (half + rec.ViewY)) / 2;
                var e = grid[cx, cy];
                // 0x141f49260: a 1 × 3 / 3 × 1 tile whose name starts with "door".
                if (e is not { } cell || cell.Tile.Width * cell.Tile.Height != 3)
                {
                    notes.Add($"heist door pass: Failed to find door tile at ({cx}, {cy}) for {d} - {(e is { } t ? Path.GetFileName(t.Tile.Path) : "empty")}");
                    continue;
                }
                var type = d.Type;
                string tileTag = type?.Tile ?? "door";
                var key = cell.Tile.Key.Rotate(cell.Orientation);
                var cands = rec.Store.Candidates(tileTag, key);
                if (cands.Count == 0)
                {
                    notes.Add($"heist door pass: No valid Heist door tiles of type {tileTag} at ({cx}, {cy}) key {key}");
                    continue;
                }
                int total = cands.Sum(c => c.Weight);
                var (tile, _) = Pick(cands, rng.Next((uint)total));
                int code = DoorDirection(d);
                if (type is { JobIn: < 0, JobOut: >= 0 }) code = (code - 2) & 3;
                int ox = cx - cell.OffsetX, oy = cy - cell.OffsetY;
                grid.Remove(cx, cy);
                grid.Place(tile, code, ox, oy, cell.HeightOffset);
                notes.Add($"  heist door {d}: {type?.Id ?? "NormalDoor"} -> {Path.GetFileName(tile.Path)} o{code} at ({ox}, {oy}) of {cands.Count} ({total})");
                int dcode = DoorDirection(d);
                var (dxv, dyv) = Dirs[dcode];
                var (pxv, pyv) = Dirs[(dcode + 1) & 3];
                foreach (var (tags, sgn) in new[] { (type?.SideTiles ?? [], 1), (type?.ExtraTiles ?? [], -1) })
                    foreach (var tag in tags)
                    {
                        int ax = cx + sgn * dxv - 2 * pxv, ay = cy + sgn * dyv - 2 * pyv, bx = cx + sgn * dxv + 2 * pxv, by = cy + sgn * dyv + 2 * pyv;
                        SideTile(grid, rec.Store, rng, notes, d, tag, Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx) + 1, Math.Max(ay, by) + 1);
                    }
            }
        }
    }

    /// <summary><c>0x141f49a30</c>(ctx, tag, rect): the first cell of the rect (x inner, y outer) holding a tile's ORIGIN whose
    /// turned key has candidates under the tag gets one of them: draw(total weight) at <c>0x141f49c34</c>, the pick's own
    /// orientation, the cell's height. A tile with a tag of its own (tile data +16: a door, or a side tile a neighbouring
    /// door already placed) is skipped.</summary>
    static bool SideTile(TileGrid grid, TileStore store, LevelRandom rng, List<string> notes, HeistDoor d, string tag, int x0, int y0, int x1, int y1)
    {
        string? t = store.HasTag(tag) ? tag : store.HasTag(tag.ToLowerInvariant()) ? tag.ToLowerInvariant() : null;
        if (t is null)
        {
            notes.Add($"heist door pass: side tile tag '{tag}' unknown to the tile store");
            return false;
        }
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                if (grid[x, y] is not { IsOrigin: true } cell) continue;
                if (cell.Tile.Tag.Length != 0) continue;
                var cands = store.Candidates(t, cell.Tile.Key.Rotate(cell.Orientation));
                if (cands.Count == 0) continue;
                int total = cands.Sum(c => c.Weight);
                var (tile, o) = Pick(cands, rng.Next((uint)total));
                grid.Remove(x, y);
                grid.Place(tile, o, x, y, cell.HeightOffset);
                notes.Add($"  heist side tile {tag} for {d}: {Path.GetFileName(tile.Path)} o{o} at ({x}, {y}) of {cands.Count} ({total})");
                return true;
            }
        notes.Add($"heist door pass: no {tag} tile found beside {d} in ({x0}, {y0})-({x1}, {y1})");
        return false;
    }
}
