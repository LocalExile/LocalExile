namespace GameServer.Data.Terrain.Generation;

/// <summary>The HeistPlugin's state as the server sends it in 0x1d's TerrainPluginState (parsed by <c>0x1424ba520</c>):
/// u32 seed, u8 HeistGeneration key, u8 HeistContracts row (above 0xfd = a blueprint), u8 HeistObjectives row (0xff =
/// none), u8 wing count, then per wing (<c>0x1424bb0e0</c>) u8 b (bit 0 the wing's flag, b &gt;&gt; 1 the job count), the
/// jobs {u8 HeistJobs row, u8}, u8 n NPC rows (0xff = none), u8 m reward types {u8 c: c &gt;&gt; 1 the HeistChestRewardTypes
/// row, c &amp; 1 kept}; then the per-NPC stat lists and the area's heist stats, which the layout does not read.</summary>
public sealed class HeistState
{
    public uint Seed;
    public int Key, Contract, Objective = -1;
    public List<HeistWingState> Wings = [];

    public bool Blueprint => Contract > 0xfd;

    public static HeistState Parse(byte[] b)
    {
        int p = 0;
        byte U8() => b[p++];
        uint U32() { uint v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4; return v; }
        var s = new HeistState { Seed = U32(), Key = U8(), Contract = U8() };
        int obj = U8();
        s.Objective = obj == 0xff ? -1 : obj;
        int wings = U8();
        for (int i = 0; i < wings; i++)
        {
            var w = new HeistWingState();
            int c = U8();
            w.Flag = c & 1;
            for (int j = 0; j < c >> 1; j++) { int job = U8(); int extra = U8(); w.Jobs.Add((job, extra)); }
            int n = U8();
            for (int j = 0; j < n; j++) { int v = U8(); w.Npcs.Add(v == 0xff ? -1 : v); }
            int m = U8();
            for (int j = 0; j < m; j++) { int v = U8(); w.Rewards.Add((v >> 1, v & 1)); }
            s.Wings.Add(w);
        }
        return s;
    }
}

public sealed class HeistWingState
{
    /// <summary>+24: bit 0 of the wing byte.</summary>
    public int Flag;
    public List<(int Job, int Extra)> Jobs = [];
    public List<int> Npcs = [];
    public List<(int Reward, int Kept)> Rewards = [];

    public bool HasJob(int job) => job >= 0 && Jobs.Any(j => j.Job == job);
}

/// <summary>The heist generator's own random stream (<c>0x1413cbe50</c>): a TinyMT (the client's step, see
/// <see cref="TinyMt"/>) seeded by init_by_array over {seed, wing index} with the client's pre-folded status words and eight
/// warm-up steps. Draws are <c>0x140484fa0</c>'s bounded draw; <see cref="Raw"/> is one tempered word
/// (<c>0x140484ed0</c>).</summary>
sealed class HeistRng
{
    const uint Mat1 = 0x8f7011ee, Mat2 = 0xfc78ff1f, TMat = 0x3793fdff;
    uint _s0, _s1, _s2, _s3;
    public int Steps;

    public HeistRng(uint seed, uint wing)
    {
        uint[] key = [seed, wing];
        uint[] st = [1077108816u, unchecked((uint)-811371972), 1017929583u, 932445695u];
        int i = 1, cnt = 0;
        static uint F1(uint x) => 1664525u * (x ^ (x >> 27));
        static uint F2(uint x) => 1566083941u * (x ^ (x >> 27));
        for (int j = 0; j < 2; j++)
        {
            uint r = F1(st[i % 4] ^ st[(i + 3) % 4] ^ st[(i + 1) % 4]);
            st[(i + 1) % 4] += r;
            r += key[j] + (uint)i;
            st[(i + 2) % 4] += r;
            st[i % 4] = r;
            i = (i + 1) % 4;
            cnt++;
        }
        while (cnt < 7)
        {
            uint r = F1(st[i % 4] ^ st[(i + 3) % 4] ^ st[(i + 1) % 4]);
            st[(i + 1) % 4] += r;
            r += (uint)i;
            st[(i + 2) % 4] += r;
            st[i % 4] = r;
            i = (i + 1) % 4;
            cnt++;
        }
        for (int k = 0; k < 4; k++)
        {
            uint r = F2(st[i % 4] + st[(i + 1) % 4] + st[(i + 3) % 4]);
            st[(i + 1) % 4] ^= r;
            r -= (uint)i;
            st[(i + 2) % 4] ^= r;
            st[i % 4] = r;
            i = (i + 1) % 4;
        }
        (_s0, _s1, _s2, _s3) = (st[0], st[1], st[2], st[3]);
        for (int k = 0; k < 8; k++) Step();
    }

    uint Step()
    {
        Steps++;
        uint x = (_s0 & 0x7fffffff) ^ _s1 ^ _s2;
        uint y = _s3 ^ (_s3 << 1);
        x = (x >> 1) ^ x ^ y;
        uint s1 = _s1;
        _s0 = s1;
        _s1 = (0u - (x & 1) & Mat1) ^ _s2;
        _s2 = (0u - (x & 1) & Mat2) ^ (x << 10) ^ y;
        _s3 = x;
        uint t = (_s2 >> 8) + s1;
        return (0u - (t & 1) & TMat) ^ t ^ x;
    }

    public uint Raw() => Step();

    public uint Draw(uint n)
    {
        if (n < 2) return 0;
        uint v;
        do v = Step();
        while (uint.MaxValue / n <= v / n && uint.MaxValue % n != n - 1);
        return v % n;
    }

    /// <summary><c>0x1404d03d0</c>(lo, hi): no draw when lo == hi.</summary>
    public int Ranged(int lo, int hi)
    {
        if (lo == hi) return lo;
        uint span = (uint)(hi - lo);
        if (span == uint.MaxValue) return (int)(Raw() + (uint)lo);
        return lo + (int)Draw(span + 1);
    }

    /// <summary>RandomElement: the index into a container of n (Draw(n) when n &gt; 1; n == 0 throws in the client).</summary>
    public int Element(int n)
    {
        if (n == 0) throw new InvalidOperationException("RandomElement called on an empty container with no default value specified");
        return n > 1 ? (int)Draw((uint)n) : 0;
    }

    public int Coin() => (int)(Raw() & 1);
}

/// <summary><c>0x141a5ef40</c>'s running pick: cnt += weight, r = Draw(cnt) (a raw word when cnt wraps to 0), the
/// candidate is taken when r &lt; weight, so the LAST winner stays.</summary>
sealed class HeistReservoir<T>(HeistRng rng)
{
    public uint Count;
    public T? Value;

    public bool Offer(T value, uint weight = 100)
    {
        Count += weight;
        uint r = Count == 0 ? rng.Raw() : rng.Draw(Count);
        if (r < weight) Value = value;
        return r < weight;
    }
}

sealed record HeistRoomRow(int Row, int Area, int Idx, string Arm, int JobIn, int JobOut, int Type, int W, int H, int GoalFlag, bool Disabled)
{
    public bool Hazard => JobIn >= 0 || JobOut >= 0;
    public bool UsesJob(int job) => job == JobIn || job == JobOut;
}

sealed record HeistDoorRow(int Row, string Id, string Tile, int JobIn, int JobOut, string[] SideTiles, string[] ExtraTiles, int Area)
{
    public bool Hazard => JobIn >= 0 || JobOut >= 0;
    public bool UsesJob(int job) => job == JobIn || job == JobOut;
}

sealed record HeistRewardRow(int Row, string Id, int LvlMin, int LvlMax, int Weight, int[] Jobs, int Kind);

sealed record HeistIntroRow(int Row, string Id, int Area, int MinLevel, int Weight, string Dgr, int Unit, int Buffer, int Overlap, int Wings, int W, int H);

sealed record HeistQuestRow(int Row, int Goal, int Link, bool NoIn, bool NoOut, int PathLen, bool NoRandomRewards, int Intro, int DoorInCount, int Entrance);

/// <summary>The heist .datc64 rows the generator reads, at the decompile's offsets (the in-memory rows are the file
/// rows; refs are 16-byte foreign rows, 0xfefe… = null).</summary>
sealed class HeistTables
{
    readonly RawDat _areas, _gen, _rooms, _doors, _intros, _rewards, _quests, _worldAreas;

    public HeistTables(Func<string, byte[]?> read)
    {
        RawDat Load(string name) => new(read($"Data/{name}.datc64") ?? throw new FileNotFoundException($"Data/{name}.datc64"));
        _areas = Load("HeistAreas"); _gen = Load("HeistGeneration"); _rooms = Load("HeistRooms"); _doors = Load("HeistDoors");
        _intros = Load("HeistIntroAreas"); _rewards = Load("HeistChestRewardTypes"); _quests = Load("HeistQuestContracts"); _worldAreas = Load("WorldAreas");
    }

    /// <summary>The HeistAreas row whose WorldAreas list (+8 / +16) holds the area (<c>0x141f3d980</c>'s map at +96), or −1.</summary>
    public int AreaFor(string worldAreaId)
    {
        int wa = _worldAreas.Find(i => _worldAreas.String(i, 0) == worldAreaId);
        if (wa < 0) return -1;
        return _areas.Find(i => _areas.RefList(i, 8).Contains(wa));
    }

    public (string Id, string WingDgr, int Unit, int Buffer, int Overlap) AreaInfo(int i) =>
        (_areas.String(i, 0), _areas.String(i, 108), _areas.I32(i, 116), _areas.I32(i, 120), _areas.U8(i, 124));

    /// <summary><c>0x1424bbc30</c>: the last HeistGeneration row with +4 == key and +0 &lt;= level (table order).</summary>
    public int GenRow(int key, int level)
    {
        int found = -1;
        for (int i = 0; i < _gen.Count; i++)
        {
            if (_gen.I32(i, 4) != key) continue;
            if (_gen.I32(i, 0) > level) break;
            found = i;
        }
        return found;
    }

    public (int Lo, int Hi) GenPair(int row, int off) => (_gen.I32(row, off), _gen.I32(row, off + 4));
    public int GenU32(int row, int off) => _gen.I32(row, off);

    public HeistRoomRow Room(int i) => new(i, _rooms.Ref(i, 0), _rooms.I32(i, 16), _rooms.String(i, 20), _rooms.Ref(i, 28), _rooms.Ref(i, 44),
        _rooms.I32(i, 60), _rooms.I32(i, 64), _rooms.I32(i, 68), _rooms.U8(i, 84), _rooms.U8(i, 85) != 0);

    /// <summary><c>0x141f3a3a0</c>'s walk: ids 0.. of this area until the first id without a row (the map at +32 keyed by
    /// (id, area); the first row of an id wins).</summary>
    public List<HeistRoomRow> AreaRooms(int areaRow)
    {
        var byIdx = new Dictionary<int, int>();
        for (int i = 0; i < _rooms.Count; i++)
            if (_rooms.Ref(i, 0) == areaRow) byIdx.TryAdd(_rooms.I32(i, 16), i);
        var result = new List<HeistRoomRow>();
        for (int idx = 0; idx < 1000 && byIdx.TryGetValue(idx, out int row); idx++) result.Add(Room(row));
        return result;
    }

    public HeistDoorRow Door(int i) => new(i, _doors.String(i, 0), _doors.String(i, 8), _doors.Ref(i, 16), _doors.Ref(i, 32),
        _doors.StringList(i, 56), _doors.StringList(i, 72), _doors.Ref(i, 92));

    List<HeistDoorRow>? _allDoors;
    public List<HeistDoorRow> AllDoors => _allDoors ??= Enumerable.Range(0, _doors.Count).Select(Door).ToList();
    public HeistDoorRow DoorById(string id) => AllDoors.First(d => d.Id == id);

    public HeistRewardRow Reward(int i) => new(i, _rewards.String(i, 0), _rewards.I32(i, 40), _rewards.I32(i, 44), _rewards.I32(i, 48),
        _rewards.RefList(i, 60), _rewards.I32(i, 80));
    public int RewardCount => _rewards.Count;
    public HeistRewardRow RewardById(string id) => Reward(_rewards.Find(i => _rewards.String(i, 0) == id));

    public HeistIntroRow Intro(int i) => new(i, _intros.String(i, 0), _intros.Ref(i, 8), _intros.I32(i, 24), _intros.I32(i, 28), _intros.String(i, 32),
        _intros.I32(i, 40), _intros.I32(i, 44), _intros.U8(i, 48), _intros.I32(i, 49), _intros.I32(i, 53), _intros.I32(i, 57));

    public List<HeistIntroRow> AreaIntros(int areaRow) =>
        Enumerable.Range(0, _intros.Count).Where(i => _intros.Ref(i, 8) == areaRow).Select(Intro).ToList();

    /// <summary>The HeistQuestContracts row of a contract (<c>0x141f3d980</c>'s map at +96), or null.</summary>
    public HeistQuestRow? Quest(int contractRow)
    {
        int i = _quests.Find(r => _quests.Ref(r, 0) == contractRow);
        if (i < 0) return null;
        return new HeistQuestRow(i, _quests.Ref(i, 77), _quests.Ref(i, 93), _quests.U8(i, 109) != 0, _quests.U8(i, 110) != 0, _quests.I32(i, 116),
            _quests.U8(i, 213) != 0, _quests.Ref(i, 214), _quests.I32(i, 230), _quests.Ref(i, 234));
    }
}

/// <summary>A wing room (the client's 56-byte entries): unit rect, HeistRooms row (null for a fill), the reward and the kind byte.</summary>
sealed class HeistRoom(int x, int y, int w, int h, HeistRoomRow? row, int kind)
{
    public int X = x, Y = y, W = w, H = h;
    public HeistRoomRow? Row = row;
    public (int Reward, int Kept)? Reward;
    public int Kind = kind;
    public (int X1, int Y1, int X2, int Y2) Rect => (X, Y, X + W, Y + H);
    public override string ToString() => $"Room({X},{Y} {W}x{H} {(Row is null ? "null" : Path.GetFileName(Row.Arm))} kind {Kind})";
}

/// <summary>A wing door (48 bytes): cell A (in the earlier room), cell B (in the later one), the two room indices, the
/// HeistDoors row and the kept byte.</summary>
sealed class HeistDoor(int ax, int ay, int bx, int by, int ia, int ib, HeistDoorRow? type, int kind)
{
    public int Ax = ax, Ay = ay, Bx = bx, By = by, Ia = ia, Ib = ib, Kind = kind;
    public HeistDoorRow? Type = type;
    public override string ToString() => $"Door(({Ax},{Ay})->({Bx},{By}) rooms {Ia}-{Ib} {Type?.Id ?? "null"})";
}

public sealed class HeistWingLayout
{
    /// <summary>The packed position (area units) and the bounding box (room units).</summary>
    public int X, Y, W, H;
    internal List<HeistRoom> Rooms = [];
    internal List<HeistDoor> Doors = [];
    public int EscapeIdx, HasEscape, Flag;
}

public sealed class HeistIntroEntry
{
    public int X, Y, W, H, Wing, Kept;
    internal HeistIntroRow Row = null!;
}

sealed class HeistException(string message) : InvalidOperationException(message);

/// <summary><c>0x141f37150</c> for one wing, every step in client order: the entrance (a type-0 room, RandomElement), the
/// goal (type 1, a reservoir), the main path of ranged(gen+8) rooms grown from the last room's free ring cells (the
/// reservoir over the ring, then a size-map entry and a room of it, placed over the chosen cell), a NormalDoor between
/// each pair, the hazard rooms (type 2 with a job) swapped into path slots of the same area, the hazard door types, the
/// reward side rooms, the job enforcement and the bounding box.</summary>
sealed class HeistWingBuilder(HeistTables t, int area, HeistWingState ws, int key, int level, bool blueprint, bool hard, HeistQuestRow? quest, int gen, HeistRng rng, List<string> notes)
{
    readonly HeistWingLayout _wing = new();
    Dictionary<(int X, int Y), int> _cells = [];
    List<int> _flags = [];
    List<HeistRoomRow>[] _lists = [], _lists0 = [];
    List<HeistRoomRow> _lists1 = [];
    List<List<HeistRoomRow>> _sizeEntries = [];
    List<HeistDoorRow> _doorsAvail = [];
    HeistDoorRow _normalDoor = null!;
    HeistRoomRow _entrance = null!, _goal = null!;
    int _entranceRot, _pathLen, _retries, _inward, _outward, _inward0, _outward0;

    // 0x141f3a3a0: the room lists.
    (SortedDictionary<long, List<HeistRoomRow>> SizeMap, List<HeistRoomRow>[] Lists) RoomLists(int inward, int outward)
    {
        var sizeMap = new SortedDictionary<long, List<HeistRoomRow>>();
        var lists = Enumerable.Range(0, 6).Select(_ => new List<HeistRoomRow>()).ToArray();
        foreach (var r in t.AreaRooms(area))
        {
            if (r.Disabled || r.Type == 6) continue;
            if (r.JobIn >= 0 && !ws.HasJob(r.JobIn)) continue;
            if (r.JobOut >= 0 && !ws.HasJob(r.JobOut)) continue;
            bool ok = (key != 0 && r.Type != 2) || ((r.JobIn < 0 || inward != 0) && (r.JobOut < 0 || outward != 0));
            if (!ok) continue;
            if (r.Type == 2 && r.JobIn < 0 && r.JobOut < 0)
            {
                Add(sizeMap, r.W, r.H, r);
                if (r.W != r.H) Add(sizeMap, r.H, r.W, r);
            }
            else lists[r.Type].Add(r);
        }
        return (sizeMap, lists);
        static void Add(SortedDictionary<long, List<HeistRoomRow>> map, int w, int h, HeistRoomRow r)
        {
            long k = ((long)w << 16) + h;
            if (!map.TryGetValue(k, out var l)) map[k] = l = [];
            l.Add(r);
        }
    }

    // 0x141f3a7a0: the door types.
    List<HeistDoorRow> DoorList(int v368, int v369)
    {
        var result = new List<HeistDoorRow>();
        if (!(v368 > 0 || v369 > 0)) return result;
        foreach (var d in t.AllDoors)
        {
            if (d.JobIn < 0 && d.JobOut < 0) continue;
            if (d.Tile == "door") continue;
            if (d.Area == area) continue;
            if (d.JobIn >= 0 && !ws.HasJob(d.JobIn)) continue;
            if (d.JobOut >= 0 && !ws.HasJob(d.JobOut)) continue;
            if (key != 0 || ((d.JobIn < 0 || v368 != 0) && (d.JobOut < 0 || v369 != 0))) result.Add(d);
        }
        return result;
    }

    // 0x141f3aec0: place a room (decrement the hazard counts, append, mark the cells).
    int Place(int x, int y, HeistRoomRow room, bool rot, int kind)
    {
        var (w, h) = rot ? (room.H, room.W) : (room.W, room.H);
        if (room.JobIn >= 0)
        {
            if (_inward == 0) throw new HeistException("Too many inwards hazards");
            if (--_inward == 0) DropHazardRooms(true);
        }
        if (room.JobOut >= 0)
        {
            if (_outward == 0) throw new HeistException("Too many outwards hazards");
            if (--_outward == 0) DropHazardRooms(false);
        }
        int idx = _wing.Rooms.Count;
        _wing.Rooms.Add(new HeistRoom(x, y, w, h, room, kind));
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++) _cells[(x + i, y + j)] = idx;
        return idx;
    }

    /// <summary><c>0x141f3ac00</c> / <c>0x141f3ad60</c>: the rooms with that hazard leave the lists (the key keeps list 2 only).</summary>
    void DropHazardRooms(bool inward)
    {
        bool Keep(HeistRoomRow r) => inward ? r.JobIn < 0 : r.JobOut < 0;
        if (key != 0) _lists[2] = _lists[2].Where(Keep).ToList();
        else for (int k = 0; k < 6; k++) _lists[k] = _lists[k].Where(Keep).ToList();
    }

    // 0x141f3ab00: a w × h rect of free cells.
    bool Free(int x, int y, int w, int h)
    {
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                if (_cells.ContainsKey((x + i, y + j))) return false;
        return true;
    }

    // 0x141f3b2a0: place a room so that it covers the chosen cell.
    bool PlaceNear((int X, int Y) cell, HeistRoomRow room, int kind)
    {
        var (cx, cy) = cell;
        var res = new HeistReservoir<(int, int)>(rng);
        int w = room.W, h = room.H;
        for (int oy = cy - h + 1; oy <= cy; oy++)
            for (int ox = cx - w + 1; ox <= cx; ox++)
                if (Free(ox, oy, w, h)) res.Offer((ox, oy));
        uint firstCnt = res.Count;
        var firstPick = res.Value;
        if (w != h)
            for (int oy = cy - w + 1; oy <= cy; oy++)
                for (int ox = cx - h + 1; ox <= cx; ox++)
                    if (Free(ox, oy, h, w)) res.Offer((ox, oy));
        if (res.Count == 0) return false;
        bool rot = firstCnt == 0 || res.Value != firstPick;
        Place(res.Value.Item1, res.Value.Item2, room, rot, kind);
        return true;
    }

    // 0x141f52990: the free ring cells around a room.
    HeistReservoir<(int, int)> RingCandidates((int X1, int Y1, int X2, int Y2) rect, bool avoidDoor)
    {
        var (x1, y1, x2, y2) = rect;
        var res = new HeistReservoir<(int, int)>(rng);
        var last = _wing.Doors.Count != 0 ? _wing.Doors[^1] : null;
        bool Ok(int x, int y)
        {
            if (_cells.ContainsKey((x, y))) return false;
            if (avoidDoor && last is not null && (last.Bx - x) * (last.Bx - x) + (last.By - y) * (last.By - y) == 1) return false;
            return true;
        }
        for (int x = x1; x < x2; x++) if (Ok(x, y1 - 1)) res.Offer((x, y1 - 1));
        for (int y = y1; y < y2; y++) if (Ok(x2, y)) res.Offer((x2, y));
        for (int x = x2 - 1; x >= x1; x--) if (Ok(x, y2)) res.Offer((x, y2));
        for (int y = y2 - 1; y >= y1; y--) if (Ok(x1 - 1, y)) res.Offer((x1 - 1, y));
        return res;
    }

    // 0x141f501a0: the free ring cells around a path room (the side-room scan).
    void SideRing((int X1, int Y1, int X2, int Y2) rect, HeistReservoir<(int Slot, (int X, int Y) Cell)> res, int slot)
    {
        var (x1, y1, x2, y2) = rect;
        for (int x = x1; x < x2; x++) if (!_cells.ContainsKey((x, y1 - 1))) res.Offer((slot, (x, y1 - 1)));
        for (int y = y1; y < y2; y++) if (!_cells.ContainsKey((x2, y))) res.Offer((slot, (x2, y)));
        for (int x = x2 - 1; x >= x1; x--) if (!_cells.ContainsKey((x, y2))) res.Offer((slot, (x, y2)));
        for (int y = y2 - 1; y >= y1; y--) if (!_cells.ContainsKey((x1 - 1, y))) res.Offer((slot, (x1 - 1, y)));
    }

    static readonly (int X, int Y)[] Around = [(0, -1), (1, 0), (0, 1), (-1, 0)];

    // 0x141f3bb80 / 0x141f3c460: a door at a neighbour cell inside a room.
    bool DoorFrom((int X, int Y) cell, (int X1, int Y1, int X2, int Y2) prev, HeistDoorRow type, int kind)
    {
        bool made = false;
        int n = _wing.Rooms.Count;
        foreach (var (dx, dy) in Around)
        {
            int nx = cell.X + dx, ny = cell.Y + dy;
            if (prev.X1 <= nx && nx < prev.X2 && prev.Y1 <= ny && ny < prev.Y2)
            {
                _wing.Doors.Add(new HeistDoor(nx, ny, cell.X, cell.Y, n - 2, n - 1, type, kind));
                made = true;
            }
        }
        return made;
    }

    bool SideDoor((int X, int Y) cell, int slot, HeistDoorRow type, int kind)
    {
        bool made = false;
        int n = _wing.Rooms.Count;
        foreach (var (dx, dy) in Around)
        {
            int nx = cell.X + dx, ny = cell.Y + dy;
            if (_cells.TryGetValue((nx, ny), out int at) && at == slot)
            {
                _wing.Doors.Add(new HeistDoor(nx, ny, cell.X, cell.Y, slot, n - 1, type, kind));
                _flags[slot]++;
                made = true;
            }
        }
        return made;
    }

    // 0x141f3b6f0: the goal room.
    HeistRoomRow? PickGoal(int flag)
    {
        var plain = new HeistReservoir<HeistRoomRow>(rng);
        var hazard = new HeistReservoir<HeistRoomRow>(rng);
        foreach (var r in _lists1)
        {
            if (r.GoalFlag != flag) continue;
            if (r.JobIn >= 0)
            {
                if (!ws.HasJob(r.JobIn)) continue;
            }
            else if (r.JobOut < 0)
            {
                plain.Offer(r);
                continue;
            }
            if (r.JobOut < 0 || ws.HasJob(r.JobOut)) hazard.Offer(r);
        }
        return hazard.Count != 0 ? hazard.Value : plain.Value;
    }

    // 0x141f3b960: the main-path reset.
    void ResetPath()
    {
        if (++_retries >= 100) throw new HeistException("Failed to generate MainPath");
        _cells = [];
        _flags = [1];
        _wing.Rooms = [];
        _wing.Doors = [];
        (_inward, _outward) = (_inward0, _outward0);
        _lists = _lists0.Select(l => l.ToList()).ToArray();
        Place(0, 0, _entrance, _entranceRot != 0, 1);
    }

    // 0x141f3be30: swap a hazard room into a path slot of the same area.
    bool HazardPass(bool wantIn, bool wantOut)
    {
        var res = new HeistReservoir<(HeistRoomRow Room, int Slot)>(rng);
        foreach (var r in _lists[2])
        {
            if (r.JobIn >= 0 && _inward == 0) continue;
            if (r.JobOut >= 0 && _outward == 0) continue;
            if ((wantIn && r.JobIn < 0) || (wantOut && r.JobOut < 0)) continue;
            for (int slot = 1; slot <= _pathLen; slot++)
            {
                var cur = _wing.Rooms[slot].Row;
                if (cur is not null && cur.Hazard) continue;
                if ((cur?.W ?? 0) * (cur?.H ?? 0) != r.W * r.H) continue;
                res.Offer((r, slot));
            }
        }
        if (res.Value.Room is { } room)
        {
            if (room.JobIn >= 0) _inward--;
            if (room.JobOut >= 0) _outward--;
            _wing.Rooms[res.Value.Slot].Row = room;
        }
        return res.Count != 0;
    }

    // 0x141f3bcc0: a door type onto a plain wing door (the reservoir runs across door types).
    void DoorSlots(HeistDoorRow type, HeistReservoir<(HeistDoorRow Type, int K)> res)
    {
        for (int k = 0; k < _wing.Doors.Count; k++)
        {
            var d = _wing.Doors[k];
            if (d.Type is null || !d.Type.Hazard) res.Offer((type, k));
        }
    }

    // 0x141f3c640: the door type of a side room.
    HeistDoorRow SideDoorType()
    {
        int chance = t.GenU32(gen, 40);
        if (_doorsAvail.Count != 0 && (chance >= 100 || rng.Draw(100) < (uint)chance))
        {
            var res = new HeistReservoir<HeistDoorRow>(rng);
            foreach (var d in _doorsAvail)
                if (d.JobIn >= 0 && d.JobOut < 0) res.Offer(d);
            if (res.Count != 0) return res.Value!;
        }
        return _normalDoor;
    }

    // 0x141f3c7d0: a reward type for a random reward room.
    HeistRewardRow? PickRewardType()
    {
        var res = new HeistReservoir<HeistRewardRow>(rng);
        for (int i = 0; i < t.RewardCount; i++)
        {
            var rt = t.Reward(i);
            if (!(rt.LvlMin <= level && level <= rt.LvlMax)) continue;
            if (rt.Kind == (hard ? 1 : 2)) continue;
            if (!rt.Jobs.Any(ws.HasJob)) continue;
            if (rt.Weight != 0) res.Offer(rt, (uint)rt.Weight);
        }
        return res.Value;
    }

    // 0x141f3c0e0: a side room beside a path room.
    bool AddSideRoom(HeistRoomRow room, HeistDoorRow type, (int Reward, int Kept)? reward, int kind)
    {
        var res = new HeistReservoir<(int Slot, (int X, int Y) Cell)>(rng);
        for (int slot = 1; slot <= _pathLen; slot++)
            if (key != 0 || _flags[slot] < 4) SideRing(_wing.Rooms[slot].Rect, res, slot);
        if (res.Count == 0) return false;
        var (slotPick, cell) = res.Value;
        if (!PlaceNear(cell, room, kind)) return false;
        _wing.Rooms[^1].Reward = reward;
        _flags.Add(1);
        if (!SideDoor(cell, slotPick, type, kind)) throw new HeistException("Failed to add door to SideRoom!");
        return true;
    }

    public HeistWingLayout Build()
    {
        var q = quest;
        (int Lo, int Hi) Pair(int off) => t.GenPair(gen, off);
        bool noIn = q is { NoIn: true }, noOut = q is { NoOut: true };
        int inward, v368, outward, v369;
        if (noIn) inward = v368 = 0;
        else
        {
            inward = q is { PathLen: not 0 } ? q.PathLen : 0;
            if (inward == 0) { var (lo, hi) = Pair(24); inward = rng.Ranged(lo, hi); }
            v368 = q is { DoorInCount: not 0 } ? q.DoorInCount : 0;
            if (v368 == 0) { var (lo, hi) = Pair(68); v368 = rng.Ranged(lo, hi); }
        }
        if (noOut) outward = v369 = 0;
        else
        {
            var (lo, hi) = Pair(32); outward = rng.Ranged(lo, hi);
            var (lo2, hi2) = Pair(76); v369 = rng.Ranged(lo2, hi2);
        }
        (_inward0, _outward0) = (inward, outward);
        (_inward, _outward) = (inward, outward);
        var (sizeMap, lists) = RoomLists(inward, outward);
        _lists0 = lists.Select(l => l.ToList()).ToArray();
        _lists = lists;
        _lists1 = lists[1].ToList();
        bool roomIn = lists[2].Any(r => r.JobIn >= 0), roomOut = lists[2].Any(r => r.JobOut >= 0);
        _doorsAvail = DoorList(v368, v369);
        bool doorIn = _doorsAvail.Any(d => d.JobIn >= 0), doorOut = _doorsAvail.Any(d => d.JobOut >= 0);
        _sizeEntries = sizeMap.Values.ToList();
        notes.Add($"  heist wing counts: inward {inward} outward {outward} door-in {v368} door-out {v369}; rooms [{string.Join(", ", lists.Select(l => l.Count))}], sizes {sizeMap.Count}, door types [{string.Join(", ", _doorsAvail.Select(d => d.Id))}]");
        // The entrance.
        if (q is { Entrance: >= 0 }) _entrance = t.Room(q.Entrance);
        else
        {
            if (lists[0].Count == 0) throw new HeistException("No possible Entrance rooms");
            _entrance = lists[0][rng.Element(lists[0].Count)];
        }
        _entranceRot = rng.Coin();
        Place(0, 0, _entrance, _entranceRot != 0, 1);
        _flags = [1];
        // The goal.
        HeistRoomRow? goal;
        if (q is { Goal: >= 0 }) goal = t.Room(q.Goal);
        else
        {
            goal = null;
            if (blueprint) goal = PickGoal(1);
            goal ??= PickGoal(0);
        }
        _goal = goal ?? throw new HeistException("No possible Goal rooms");
        _normalDoor = t.DoorById("NormalDoor");
        if (q is { PathLen: not 0 }) _pathLen = q.PathLen;
        else { var (lo, hi) = Pair(8); _pathLen = rng.Ranged(lo, hi); }
        _retries = 0;
        notes.Add($"  heist wing entrance {Path.GetFileName(_entrance.Arm)} rot {_entranceRot}, goal {Path.GetFileName(_goal.Arm)}, path {_pathLen}");
        // The main path.
        while (_wing.Rooms.Count < _pathLen + 2)
        {
            var last = _wing.Rooms[^1];
            bool avoid = last.W > 1 && last.H > 1 && _wing.Doors.Count != 0;
            var res = RingCandidates(last.Rect, avoid);
            if (res.Count != 0)
            {
                HeistRoomRow room;
                if (_wing.Rooms.Count == _pathLen + 1) room = _goal;
                else
                {
                    var entry = _sizeEntries[rng.Element(_sizeEntries.Count)];
                    room = entry[rng.Element(entry.Count)];
                }
                var prev = last.Rect;
                if (!PlaceNear(res.Value, room, 1)) { ResetPath(); continue; }
                if (!DoorFrom(res.Value, prev, _normalDoor, 1)) throw new HeistException("Failed to create door");
            }
            else ResetPath();
        }
        _flags = [1];
        for (int i = 0; i < _pathLen; i++) _flags.Add(2);
        _flags.Add(1);
        // The inward hazards.
        while (_inward != 0 && HazardPass(true, false)) { }
        int v89 = _inward + v368, v88 = v369;
        while (v89 != 0)
        {
            var res = new HeistReservoir<(HeistDoorRow Type, int K)>(rng);
            foreach (var d in _doorsAvail)
                if ((d.JobIn < 0 || v89 != 0) && (d.JobOut < 0 || v88 != 0) && d.JobIn >= 0) DoorSlots(d, res);
            if (res.Value.Type is null) break;
            var (type, k) = res.Value;
            if (type.JobIn >= 0) v89--;
            if (type.JobOut >= 0) v88--;
            _wing.Doors[k].Type = type;
        }
        _inward = v89;
        if (v89 != 0)
        {
            while (HazardPass(true, false) && _inward != 0) { }
            if (_inward != 0 && doorIn && roomIn && key != 0) throw new HeistException($"Failed to generate {_inward} inwards Hazards");
        }
        // The outward hazards.
        while (_outward != 0 && HazardPass(false, true)) { }
        int v97 = 0, v100 = _outward + v88;
        while (v100 != 0)
        {
            var res = new HeistReservoir<(HeistDoorRow Type, int K)>(rng);
            foreach (var d in _doorsAvail)
                if ((d.JobIn < 0 || v97 != 0) && d.JobOut >= 0 && v100 != 0) DoorSlots(d, res);
            if (res.Value.Type is null) break;
            var (type, k) = res.Value;
            if (type.JobIn >= 0) v97--;
            if (type.JobOut >= 0) v100--;
            _wing.Doors[k].Type = type;
        }
        _outward = v100;
        if (v100 != 0)
        {
            while (HazardPass(false, true) && _outward != 0) { }
            if (_outward != 0 && doorOut && roomOut && key != 0) throw new HeistException($"Failed to generate {_outward} outwards Hazards");
        }
        // The side rooms.
        if (lists[3].Count == 0) throw new HeistException("No SideRooms available");
        if (lists[5].Count == 0) throw new HeistException("No EscapeRooms available");
        int escape = t.RewardById("EscapeRoom").Row;
        foreach (var (rt, kept) in ws.Rewards)
        {
            var list = rt == escape ? lists[5] : lists[3];
            var room = list[rng.Element(list.Count)];
            var type = SideDoorType();
            if (!AddSideRoom(room, type, (rt, kept), kept)) throw new HeistException("Failed to generate enough reward rooms");
        }
        if (!blueprint || key == 0)
        {
            int cap = q is { NoRandomRewards: true } ? 0 : 99;
            var (lo, hi) = Pair(16);
            int n = Math.Min(Math.Max(1, rng.Ranged(lo, hi)), cap);
            for (int i = 0; i < n; i++)
            {
                var room = lists[3][rng.Element(lists[3].Count)];
                var type = SideDoorType();
                var rt = PickRewardType();
                if (!AddSideRoom(room, type, rt is null ? null : (rt.Row, 1), 1)) throw new HeistException("Failed to generate enough random reward rooms");
            }
        }
        EnforceJobs();
        // The bounding box.
        var rooms = _wing.Rooms;
        int minX = rooms.Min(r => r.X), minY = rooms.Min(r => r.Y), maxX = rooms.Max(r => r.X + r.W), maxY = rooms.Max(r => r.Y + r.H);
        (_wing.W, _wing.H) = (maxX - minX, maxY - minY);
        foreach (var r in rooms) { r.X -= minX; r.Y -= minY; }
        foreach (var d in _wing.Doors) { d.Ax -= minX; d.Ay -= minY; d.Bx -= minX; d.By -= minY; }
        _wing.Flag = ws.Flag;
        return _wing;
    }

    /// <summary><c>0x141f37150</c>'s last phase: every wing job must be used by a room or a door.</summary>
    void EnforceJobs()
    {
        var rooms = _wing.Rooms;
        var doors = _wing.Doors;
        foreach (var (job, _) in ws.Jobs)
        {
            bool used = rooms.Any(r => r.Row is { Type: 2 } row && row.UsesJob(job)) || doors.Any(d => d.Type is { } type && type.UsesJob(job));
            if (used) continue;
            var res = new HeistReservoir<HeistDoorRow>(rng);
            foreach (var d in _doorsAvail)
                if (d.JobIn >= 0 && d.UsesJob(job)) res.Offer(d);
            var typeIn = res.Value;
            if (typeIn is not null && JobDoors(true) is { } cands)
            {
                doors[cands[rng.Element(cands.Count)]].Type = typeIn;
                continue;
            }
            res = new HeistReservoir<HeistDoorRow>(rng);
            foreach (var d in _doorsAvail)
                if (d.JobOut >= 0 && d.UsesJob(job)) res.Offer(d);
            var typeOut = res.Value;
            if (typeOut is not null && JobDoors(false) is { } cands2)
            {
                doors[cands2[rng.Element(cands2.Count)]].Type = typeOut;
                continue;
            }
            var rres = new HeistReservoir<HeistRoomRow>(rng);
            foreach (var r in _lists0[2])
                if (r.JobIn >= 0 && r.UsesJob(job)) rres.Offer(r);
            if (rres.Value is { } roomIn && JobRooms(true) is { } rc)
            {
                int k = rc[rng.Element(rc.Count)];
                if ((rooms[k].W, rooms[k].H) == (roomIn.W, roomIn.H)) { rooms[k].Row = roomIn; continue; }
            }
            rres = new HeistReservoir<HeistRoomRow>(rng);
            foreach (var r in _lists0[2])
                if (r.JobOut >= 0 && r.UsesJob(job)) rres.Offer(r);
            if (rres.Value is { } roomOut && JobRooms(false) is { } rc2)
            {
                int k = rc2[rng.Element(rc2.Count)];
                if ((rooms[k].W, rooms[k].H) == (roomOut.W, roomOut.H)) { rooms[k].Row = roomOut; continue; }
            }
            if (typeIn is not null)
            {
                var sres = new HeistReservoir<(HeistDoorRow Type, int K)>(rng);
                DoorSlots(typeIn, sres);
                if (sres.Value.Type is not null) { doors[sres.Value.K].Type = typeIn; continue; }
            }
            if (typeOut is not null)
            {
                var sres = new HeistReservoir<(HeistDoorRow Type, int K)>(rng);
                DoorSlots(typeOut, sres);
                if (sres.Value.Type is not null) { doors[sres.Value.K].Type = typeOut; continue; }
            }
            throw new HeistException($"Failed to find a way to use job {job} in HeistArea");
        }
    }

    /// <summary>The v366 map: job → the in / out door and room index lists (the client's map is sorted by the row pointer:
    /// the dat row order, so the job ids ascending).</summary>
    SortedDictionary<int, (List<int> InDoors, List<int> OutDoors, List<int> InRooms, List<int> OutRooms)> JobMap()
    {
        var m = new SortedDictionary<int, (List<int>, List<int>, List<int>, List<int>)>();
        (List<int>, List<int>, List<int>, List<int>) Entry(int job)
        {
            if (!m.TryGetValue(job, out var e)) m[job] = e = ([], [], [], []);
            return e;
        }
        for (int k = 0; k < _wing.Rooms.Count; k++)
        {
            if (_wing.Rooms[k].Row is not { Type: 2 } row) continue;
            if (row.JobIn < 0 && row.JobOut >= 0) Entry(row.JobOut).Item4.Add(k);
            else if (row.JobIn >= 0 && row.JobOut < 0) Entry(row.JobIn).Item3.Add(k);
        }
        for (int k = 0; k < _wing.Doors.Count; k++)
        {
            if (_wing.Doors[k].Type is not { } type) continue;
            if (type.JobIn < 0 && type.JobOut >= 0) Entry(type.JobOut).Item2.Add(k);
            else if (type.JobIn >= 0 && type.JobOut < 0) Entry(type.JobIn).Item1.Add(k);
        }
        return m;
    }

    List<int>? JobDoors(bool inDoors)
    {
        foreach (var (_, e) in JobMap())
        {
            var list = inDoors ? e.InDoors : e.OutDoors;
            if (list.Count > 1) return list;
        }
        return null;
    }

    List<int>? JobRooms(bool inRooms)
    {
        foreach (var (_, e) in JobMap())
        {
            var list = inRooms ? e.InRooms : e.OutRooms;
            if (list.Count > 1) return list;
        }
        return null;
    }
}

/// <summary>The heist contract level the HeistPlugin builds from the server's state (<c>0x141f3d980</c>): the intro
/// pick (<c>0x141f3e350</c>, a weighted reservoir over the area's HeistIntroAreas rows with that wing count), each wing
/// (<see cref="HeistWingBuilder"/>), the prune (<c>0x141f3d500</c>) and the packing (<c>0x141f3cc20</c>: wings then intros, each
/// at the right-of / below corner of a placed rect that keeps the bounding box smallest, then a mirror so the first
/// intro sits top-left). The intro and the wings become "graph" nodes on the area's empty main graph
/// (<c>0x141f3eea0</c>), whose size is the packed size; a wing's record is sized by its room bounding box
/// (<c>0x141f405f0</c>). Verified on the server corpus's 12 contracts.</summary>
public sealed class HeistLevel
{
    internal readonly HeistTables Tables;
    public readonly HeistState State;
    public readonly int Level, Area;
    public readonly string AreaId, WingDgr;
    readonly int _unit, _buffer, _overlap, _gen;
    readonly HeistQuestRow? _quest;
    readonly bool _hard;
    public readonly List<HeistWingLayout> Wings = [];
    public readonly List<HeistIntroEntry> Intros = [];
    public (int W, int H) Size;
    readonly Dictionary<HeistIntroEntry, (int W, int H)> _introDims = [];
    readonly Dictionary<HeistWingLayout, (int W, int H)> _wingDims = [];

    HeistLevel(HeistTables tables, HeistState state, string worldAreaId, int level, bool hard)
    {
        Tables = tables; State = state; Level = level; _hard = hard;
        Area = tables.AreaFor(worldAreaId);
        if (Area < 0) throw new HeistException("Invalid Heist area");
        (AreaId, WingDgr, _unit, _buffer, _overlap) = tables.AreaInfo(Area);
        _gen = tables.GenRow(state.Key, level);
        if (_gen < 0) throw new HeistException($"No HeistGeneration data found for level {level}");
        if (state.Wings.Count == 0) throw new HeistException("No wings");
        _quest = tables.Quest(state.Contract);
    }

    public static HeistLevel? Build(Func<string, byte[]?> read, string worldAreaId, int level, HeistState state, List<string> notes)
    {
        var tables = new HeistTables(read);
        if (tables.AreaFor(worldAreaId) < 0) return null;
        var lv = new HeistLevel(tables, state, worldAreaId, level, hard: false);
        lv.Run(notes);
        return lv;
    }

    // 0x141f3e350.
    void PickIntro(int wings, int wingIdx, int kept, List<string> notes)
    {
        var rng = new HeistRng(State.Seed, (uint)wingIdx);
        var res = new HeistReservoir<HeistIntroRow>(rng);
        foreach (var row in Tables.AreaIntros(Area))
            if (Level >= row.MinLevel && row.Weight > 0 && row.Wings == wings) res.Offer(row, (uint)row.Weight);
        if (res.Count == 0) throw new HeistException("No intro area found for area");
        var pick = res.Value!;
        Intros.Add(new HeistIntroEntry { W = pick.W, H = pick.H, Row = pick, Wing = wingIdx, Kept = kept });
        notes.Add($"  heist intro: {Path.GetFileName(pick.Dgr)} (wing {wingIdx:x}, {res.Count / 100} candidates)");
    }

    void Run(List<string> notes)
    {
        int n = State.Wings.Count;
        if (_quest is { Intro: >= 0 })
        {
            var row = Tables.Intro(_quest.Intro);
            Intros.Add(new HeistIntroEntry { W = row.W, H = row.H, Row = row, Wing = 0xff, Kept = 1 });
        }
        else PickIntro(n, 0xff, 1, notes);
        int escape = Tables.RewardById("EscapeRoom").Row;
        for (int j = 0; j < n; j++)
        {
            var ws = State.Wings[j];
            bool hasEscape = ws.Rewards.Any(r => r.Reward == escape && r.Kept != 0);
            int escapeIdx = 0;
            if (j + 1 < n)
            {
                int kept = hasEscape && ws.Flag != 0 && State.Wings[j + 1].Flag != 0 ? 1 : 0;
                if (kept != 0) escapeIdx = Intros.Count;
                PickIntro(1, j, kept, notes);
            }
            var rng = new HeistRng(State.Seed, (uint)j);
            notes.Add($"heist wing {j}: seed {State.Seed} jobs [{string.Join(", ", ws.Jobs.Select(x => $"{x.Job}:{x.Extra}"))}] rewards [{string.Join(", ", ws.Rewards.Select(x => $"{x.Reward}:{x.Kept}"))}] flag {ws.Flag}");
            var built = new HeistWingBuilder(Tables, Area, ws, State.Key, Level, State.Blueprint, _hard, _quest, _gen, rng, notes).Build();
            built.HasEscape = hasEscape ? 1 : 0;
            built.EscapeIdx = escapeIdx;
            built.Flag = j == 0 || ws.Flag != 0 ? 1 : 0;
            Wings.Add(built);
            notes.Add($"  heist wing rooms [{string.Join(", ", built.Rooms)}]");
            notes.Add($"  heist wing doors [{string.Join(", ", built.Doors)}]");
            notes.Add($"  heist wing bbox {built.W}x{built.H}, rng steps {rng.Steps}");
        }
        Prune();
        Pack();
    }

    // 0x141f3d500.
    void Prune()
    {
        foreach (var w in Wings)
        {
            w.Rooms = w.Rooms.Where(r => r.Kind != 0).ToList();
            w.Doors = w.Doors.Where(d => d.Kind != 0).ToList();
            if (w.EscapeIdx < Intros.Count && Intros[w.EscapeIdx].Kept == 0) w.EscapeIdx = 0;
        }
        int m = 0;
        while (m < Intros.Count)
        {
            if (Intros[m].Kept != 0) { m++; continue; }
            Intros.RemoveAt(m);
            foreach (var w in Wings)
                if (w.EscapeIdx != 0 && w.EscapeIdx >= m) w.EscapeIdx--;
        }
        int k = 0;
        while (k < Wings.Count)
        {
            if (Wings[k].Flag != 0) { k++; continue; }
            Wings.RemoveAt(k);
            foreach (var e in Intros)
                if (e.Wing != 0xff && e.Wing >= k) e.Wing--;
        }
    }

    (int W, int H) WingSize(HeistWingLayout w)
    {
        int u = _unit - _overlap;
        int a = Math.Max(1, 2 * _buffer + u * w.W), b = Math.Max(1, 2 * _buffer + u * w.H);
        return ((a + u - 1) / u, (b + u - 1) / u);
    }

    (int W, int H) IntroSize(HeistIntroEntry e)
    {
        int u = _unit - _overlap;
        var row = e.Row;
        int iu = row.Unit - row.Overlap;
        int a = Math.Max(1, row.Overlap + 2 * row.Buffer + e.W * iu - _overlap), b = Math.Max(1, row.Overlap + 2 * row.Buffer + e.H * iu - _overlap);
        return ((a + u - 1) / u, (b + u - 1) / u);
    }

    // 0x141f3cc20 / 0x141f3d220 / 0x141f3d130.
    void Pack()
    {
        var placed = new List<(int X1, int Y1, int X2, int Y2)>();
        int bx1 = 1 << 30, by1 = 1 << 30, bx2 = -(1 << 30), by2 = -(1 << 30);
        (int X, int Y) Put(int w, int h)
        {
            (int X, int Y) pos;
            if (placed.Count == 0) pos = (0, 0);
            else
            {
                uint? best = null;
                (int X, int Y)? at = null;
                foreach (var (px1, py1, px2, py2) in placed)
                    foreach (var (cx, cy) in new[] { (px2, py1), (px1, py2) })
                    {
                        var r = (cx, cy, cx + w, cy + h);
                        if (placed.Any(o => !(r.Item3 <= o.X1 || r.cx >= o.X2 || r.Item4 <= o.Y1 || r.cy >= o.Y2))) continue;
                        int ux1 = Math.Min(bx1, r.cx), uy1 = Math.Min(by1, r.cy), ux2 = Math.Max(bx2, r.Item3), uy2 = Math.Max(by2, r.Item4);
                        uint area = unchecked((uint)((ux2 - ux1) * (uy2 - uy1)));
                        if (best is null || area < best) { best = area; at = (cx, cy); }
                    }
                if (at is null || at == (0, 0)) throw new HeistException("Failed to pack Heist intro or wing");
                pos = at.Value;
            }
            placed.Add((pos.X, pos.Y, pos.X + w, pos.Y + h));
            bx1 = Math.Min(bx1, pos.X); by1 = Math.Min(by1, pos.Y); bx2 = Math.Max(bx2, pos.X + w); by2 = Math.Max(by2, pos.Y + h);
            return pos;
        }
        foreach (var w in Wings)
        {
            var size = WingSize(w);
            (w.X, w.Y) = Put(size.W, size.H);
            _wingDims[w] = size;
        }
        foreach (var e in Intros)
        {
            var size = IntroSize(e);
            (e.X, e.Y) = Put(size.W, size.H);
            _introDims[e] = size;
        }
        int W = bx2 - bx1, H = by2 - by1;
        Size = (W, H);
        if (Intros.Count != 0)
        {
            var e0 = Intros[0];
            var (iw, ih) = IntroSize(e0);
            if (e0.X > W - iw - e0.X)
            {
                foreach (var e in Intros) e.X = W - IntroSize(e).W - e.X;
                foreach (var w in Wings) w.X = W - _wingDims[w].W - w.X;
            }
            if (e0.Y > H - ih - e0.Y)
            {
                foreach (var e in Intros) e.Y = H - IntroSize(e).H - e.Y;
                foreach (var w in Wings) w.Y = H - _wingDims[w].H - w.Y;
            }
            for (int i = 1; i < Intros.Count; i++)
            {
                var a = Intros[0]; var b = Intros[i];
                if ((b.W, b.H) == (a.W, a.H) && b.X < a.X && b.Y <= a.Y) ((a.X, a.Y), (b.X, b.Y)) = ((b.X, b.Y), (a.X, a.Y));
            }
            for (int i = 1; i < Intros.Count; i++)
            {
                var a = Intros[0]; var b = Intros[i];
                if ((b.W, b.H) == (a.W, a.H) && b.Y < a.Y && b.X <= a.X) ((a.X, a.Y), (b.X, b.Y)) = ((b.X, b.Y), (a.X, a.Y));
            }
        }
    }

    /// <summary><c>0x141f3eea0</c>'s graph nodes for the main graph: (path, x, y, w, h) in area units, intros then wings,
    /// each tied to its intro entry or wing layout.</summary>
    public IEnumerable<(string Path, int X, int Y, int W, int H, object Part)> GraphNodes()
    {
        foreach (var e in Intros)
        {
            var (w, h) = _introDims[e];
            yield return (e.Row.Dgr, e.X, e.Y, w, h, e);
        }
        foreach (var wg in Wings)
        {
            var (w, h) = _wingDims[wg];
            yield return (WingDgr, wg.X, wg.Y, w, h, wg);
        }
    }

    /// <summary>Rebuilds the main graph as <c>0x141f3eea0</c> does: the packed size, one "graph" node per intro and wing at the
    /// unit centre with [path, w, h].</summary>
    public void MainGraph(TileGraph graph)
    {
        (graph.Width, graph.Height) = Size;
        graph.Nodes = [];
        graph.Edges = [];
        foreach (var (path, x, y, w, h, part) in GraphNodes())
            graph.Nodes.Add(new GraphNode { X = 23 * x + 11, Y = 23 * y + 11, Tag = "graph", Orientation = 0, Descriptions = [path, w.ToString(), h.ToString()], Plugin = part });
    }

    /// <summary><c>0x141f21b50</c>: 1 east (dx &gt; |dy|), 3 west (−dx &gt; |dy|), 2 south (dy &gt; 0), 0 north, 4 the same point.</summary>
    static int DirectionCode(int dx, int dy)
    {
        if (dx > 0) return dx > Math.Abs(dy) ? 1 : dy > 0 ? 2 : 0;
        if (dx < 0) return -dx > Math.Abs(dy) ? 3 : dy > 0 ? 2 : 0;
        return dy > 0 ? 2 : dy == 0 ? 4 : 0;
    }

    /// <summary><c>0x141f3f650</c> on an intro graph before its layout: the main intro (index 0) strips "telepad" / "teleport"
    /// from the "entrance" node's descriptions and gives it "default", then STABLE-PARTITIONS the node list so the nodes
    /// east or south of the entrance come first; an escape intro (index &gt; 0) strips "default" from every node. Then every
    /// tagged node whose tag is the HeistAreas id (the wing exits) loses "telepad" / "teleport" and gains "telepad{n}" /
    /// "teleport{m}-%areaname%" (the main intro numbers them 1..; the exits beyond the wing count get exist 0; an escape
    /// intro's exit links its wing).</summary>
    public void IntroEdits(TileGraph g, HeistIntroEntry entry, List<string> notes)
    {
        int introIndex = Intros.IndexOf(entry), nWings = Wings.Count, exits = 0;
        if (introIndex == 0)
        {
            var ent = g.Nodes.FirstOrDefault(n => n.Tag == "entrance");
            if (ent is not null)
            {
                ent.Descriptions.RemoveAll(d => d is "telepad" or "teleport");
                if (!ent.Descriptions.Contains("default")) ent.Descriptions.Add("default");
                var first = g.Nodes.Where(n => DirectionCode(n.X - ent.X, n.Y - ent.Y) is 1 or 2).ToList();
                g.Nodes = first.Concat(g.Nodes.Where(n => !first.Contains(n))).ToList();
                notes.Add($"  heist intro nodes partitioned around the entrance: [{string.Join(", ", g.Nodes.Select(n => n.Tag))}]");
            }
        }
        else
            foreach (var n in g.Nodes) n.Descriptions.RemoveAll(d => d == "default");
        foreach (var n in g.Nodes)
        {
            if (n.Tag.Length == 0) continue;
            if (g.Nodes.Count != 1 && n.Tag != AreaId) continue;
            if (exits < nWings)
            {
                n.Descriptions.RemoveAll(d => d is "telepad" or "teleport");
                if (introIndex != 0)
                {
                    n.Descriptions.Add($"telepad{introIndex + 2 * nWings}");
                    n.Descriptions.Add($"teleport{nWings + (entry.Wing != 0xff ? entry.Wing : 0) + 2}-%areaname%");
                }
                else
                {
                    n.Descriptions.Add($"telepad{exits + 1}");
                    n.Descriptions.Add($"teleport{nWings + exits + 1}-%areaname%");
                }
                exits++;
            }
            else n.Exist = 0;
        }
    }
}
