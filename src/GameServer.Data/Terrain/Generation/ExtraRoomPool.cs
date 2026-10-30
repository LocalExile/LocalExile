namespace GameServer.Data.Terrain.Generation;

/// <summary>A row of Data/ExtraTerrainFeatures.dat as the pool holds it: id (+0), rooms (+8, .arm), tiles (+0x18,
/// .tdt / .tst), room orientations (+0x39, 8 = any), the linked row added with it (+0x49), the world area it leads to (+0x51,
/// a WorldAreas row: a sub-area, <c>0x142047540</c>) and the oversize flag (+0x61: a telepad injector's room,
/// <c>0x142101e50</c>).</summary>
sealed record ExtraFeatureRow(string Name, IReadOnlyList<string> Rooms, IReadOnlyList<string> Tiles, IReadOnlyList<int> Orientations,
    int Link, int WorldArea, bool Oversize)
{
    /// <summary><c>0x142102170</c>'s orientation mask of the row: 0 (any) when it lists 8, else a bit per orientation.</summary>
    public int Mask => Orientations.Contains(8) ? 0 : Orientations.Aggregate(0, (m, o) => m | 1 << (o & 31));
}

/// <summary>The generator's extra room pool (+0x40, <c>0x141d8ae40</c> …): the extra terrain features' rows waiting for
/// a place (+0x48, in the order the features rolled), the rooms injected into mission rooms (+0, 0x28-byte entries:
/// room, orientation, row), how many subgraph weight is left to share the rows over (+0x140) and how many rows the
/// current subgraph may place (+0x144).</summary>
sealed class ExtraRoomPool
{
    public readonly List<ExtraFeatureRow> Rows = [];
    public readonly List<(DungeonRoom Room, int Orientation, ExtraFeatureRow Row)> Injected = [];
    public int Total, Share;

    /// <summary>The shrine count the features rolled (generator +0x188 / pool +0x148), less the shrines placed.</summary>
    public int Shrines;

    /// <summary>The shrines placed (+0x14a).</summary>
    public int ShrinesPlaced;

    readonly Func<string, byte[]?> _read;
    RawDat? _table;

    ExtraRoomPool(Func<string, byte[]?> read) => _read = read;

    RawDat Table => _table ??= new RawDat(_read("Data/ExtraTerrainFeatures.datc64") ?? throw new FileNotFoundException("Data/ExtraTerrainFeatures.datc64"));

    /// <summary><c>0x14204a570</c>'s adds (<c>0x141f28aa0</c>: by id): every rolled feature that is a row, in roll order.
    /// The features without a row (the Vaal side area flag, the discoverable hideout, the shrine count) stay out.</summary>
    public static ExtraRoomPool Create(Func<string, byte[]?> read, IEnumerable<ExtraTerrainFeatures.Feature> features)
    {
        var list = features.ToList();
        var pool = new ExtraRoomPool(read) { Shrines = Count(list, "(shrines)") };
        foreach (var f in list)
            if (!f.Name.StartsWith('(') && f.Name != ExtraTerrainFeatures.VaalSideArea) pool.Add(f.Name, f.Count);
        return pool;

        static int Count(List<ExtraTerrainFeatures.Feature> l, string name) => l.Where(f => f.Name == name).Sum(f => f.Count);
    }

    ExtraFeatureRow Row(int row) => new(Table.String(row, 0), Table.StringList(row, 8), Table.StringList(row, 0x18),
        Table.I32List(row, 0x39), Table.Ref8(row, 0x49), Table.Ref(row, 0x51), Table.Bool(row, 0x61));

    /// <summary>AddExtraTerrainFeature by id: <see cref="Add(ExtraFeatureRow, int)"/> for the row named
    /// <paramref name="name"/>.</summary>
    public void Add(string name, int count)
    {
        int row = Table.Find(r => Table.String(r, 0) == name);
        if (row < 0) throw new InvalidDataException($"no ExtraTerrainFeatures row {name}");
        Add(Row(row), count);
    }

    /// <summary><c>0x141d8ae40</c>: <paramref name="count"/> copies of the row at the end of the list (its tiles go to the
    /// pool's own tile store, +0x90), then the row it links to, as many times. The Shrine row is capped by the shrine
    /// count: with the Shrine rows already waiting or injected (<c>0x141d8ace0</c>), at most that many; none, and the
    /// link is not added either.</summary>
    void Add(ExtraFeatureRow row, int count)
    {
        if (count == 0) return;
        if (row.Rooms.Count == 0) return;
        if (row.Name == Shrine)
        {
            int have = Rows.Count(r => r.Name == Shrine) + Injected.Count(i => i.Row.Name == Shrine);
            if (Shrines < have + count) count = Math.Max(0, Shrines - have);
            if (count == 0) return;
        }
        for (int i = 0; i < count; i++) Rows.Add(row);
        if (row.Tiles.Count != 0) HasTiles = true;
        if (row.Link >= 0) Add(Row(row.Link), count);
    }

    /// <summary>A row added tiles to the pool's tile store (+0x90): the second store a fill room's tagged slot falls
    /// back to (<c>0x141fe89e0</c>). The tiles stay when their row leaves.</summary>
    public bool HasTiles { get; private set; }

    /// <summary><c>0x141d8bba0</c>: the subgraph's share of the rows by its weight (its master's +0x152): all of them
    /// when no weight is left to share by; else rows × weight / left, plus one when <c>Next(left)</c> falls below the
    /// remainder; the weight is then taken from what is left.</summary>
    public void Spread(int weight, LevelRandom rng)
    {
        if (Total == 0)
        {
            Share = Rows.Count;
            return;
        }
        if (weight == 0)
        {
            Share = 0;
            return;
        }
        weight = Math.Min(weight, Total);
        Share = Rows.Count * weight / Total;
        int rest = Rows.Count * weight % Total;
        if (rest != 0 && rng.Next((uint)Total) < (uint)rest) Share++;
        Total -= weight;
    }

    /// <summary><c>0x141d8b2c0</c>: the row leaves the list (its first copy); with <paramref name="counted"/> the
    /// share drops by one.</summary>
    public void Remove(ExtraFeatureRow row, bool counted)
    {
        Rows.Remove(row);
        if (counted && Share != 0) Share--;
    }

    /// <summary>The pool's placed rooms (+0x60, <c>0x141d8abb0</c>): map rectangles of the unique rooms and shrines
    /// placed. The overlays keep off them (exclusion bit 0x20, <c>0x141ffe240</c>).</summary>
    public readonly List<(int X0, int Y0, int X1, int Y1)> Placed = [];

    /// <summary><c>0x141d8abb0</c>: a room of w × h at map (x, y) was placed for a row.</summary>
    public void RoomPlaced(int x, int y, int w, int h) => Placed.Add((x, y, x + w, y + h));

    /// <summary><c>0x141d8b9a0</c>: a shrine room was placed. The shrine count drops by one (not below 0), the placed
    /// count goes up, and the first Shrine row leaves the list (the share stays). A shrine with a size (map position and
    /// turned size) goes to the placed rooms; the layout's shrine rooms (<c>0x142103490</c>) give none here (their
    /// position is the shrine object's, the size not ported).</summary>
    public void ShrinePlaced(int x = 0, int y = 0, int w = 0, int h = 0)
    {
        if (w > 0 && h > 0) RoomPlaced(x, y, w, h);
        if (Shrines != 0) Shrines--;
        ShrinesPlaced++;
        int i = Rows.FindIndex(r => r.Name == Shrine);
        if (i >= 0) Rows.RemoveAt(i);
    }

    const string Shrine = "Shrine";

    /// <summary><c>0x141d8b450</c>: an injected room whose turned size is w × h, <c>Next(count)</c> among them (in list
    /// order); it leaves the list. Null when none fits.</summary>
    public (DungeonRoom Room, int Orientation, ExtraFeatureRow Row)? Take(int w, int h, LevelRandom rng)
    {
        var fits = Injected.Where(i => Orientation.Size(i.Orientation, i.Room.Def!.Width, i.Room.Def.Height) == (w, h)).ToList();
        if (fits.Count == 0) return null;
        var pick = fits[(int)rng.Next((uint)fits.Count)];
        Injected.Remove(pick);
        return pick;
    }
}
