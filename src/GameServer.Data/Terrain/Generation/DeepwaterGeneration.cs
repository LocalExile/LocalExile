namespace GameServer.Data.Terrain.Generation;

/// <summary>The client's DeepwaterPlugin (TerrainPlugins row "DeepwaterPlugin", factory <c>0x141d93260</c>, ctor
/// <c>0x141f32690</c>): the Allflame encounter areas (WorldAreas TerrainPlugin) are an N × N grid of rooms the server
/// sends as the plugin's state in 0x1d's extra data (Agents/docs/re/011-level-generation.md §3c4). Vtable +0x20
/// (<c>0x141f32870</c>) sizes the main record and adds DeepwaterHub as a sub-area, +0x30 (<c>0x141f32a70</c>) rewrites the
/// main graph and each cell's graph, +0x28 (<c>0x141f331e0</c> → <c>0x141f34ee0</c>) joins the open sides of
/// neighbouring cells.</summary>
public sealed class DeepwaterGeneration
{
    /// <summary>The TerrainPlugins row the generator selects this pass by.</summary>
    public const string PluginId = "DeepwaterPlugin";
    /// <summary>The sub-area the plugin adds (the boat, record 1).</summary>
    public const string Hub = "DeepwaterHub";
    /// <summary>The record of cell 0, whose node <c>Prepare</c> (<c>0x141f32a70</c>) tags <c>default</c> and
    /// <c>killzone=3</c>: the level's start. Records 2 … N² + 1 are the cells in node order; record 1 is the boat (its
    /// node also says <c>default</c>). The plugin's other start marks, <c>killzone=3</c> and the Bathysphere
    /// (Objects/ExtractionObject, <c>landed</c> = 2), belong to the lanterns and the extraction.</summary>
    public const int StartRecord = 2;

    /// <summary>A cell: its DeepwaterRooms row, the orientation byte (cell +0x10, the room node's orientation) and its
    /// (stat id, value) pairs, which generation does not read.</summary>
    public sealed record Cell(int Room, byte Orientation, IReadOnlyList<(uint Stat, uint Value)> Stats);

    /// <summary>N (plugin +0x58).</summary>
    public int Size { get; }
    /// <summary>The N × N cells, row-major (N·y + x).</summary>
    public IReadOnlyList<Cell> Cells { get; }

    readonly TerrainCache files;
    readonly RawDat rooms, biomes;
    readonly string mainGraph;
    readonly Func<int, IReadOnlyList<TopologyInfo>?> areaTopologies;

    /// <param name="mainGraph">The area's picked topology (record 0's graph).</param>
    /// <param name="state">The plugin state 0x1d sent, null or empty for none (the client's default grid).</param>
    /// <param name="areaTopologies">A WorldAreas row's topologies, by row index.</param>
    public DeepwaterGeneration(TerrainCache files, string mainGraph, byte[]? state, Func<int, IReadOnlyList<TopologyInfo>?> areaTopologies)
    {
        this.files = files;
        this.mainGraph = mainGraph;
        this.areaTopologies = areaTopologies;
        rooms = new RawDat(files.Raw("Data/DeepwaterRooms.datc64") ?? throw new FileNotFoundException("Data/DeepwaterRooms.datc64"));
        biomes = new RawDat(files.Raw("Data/DeepwaterBiomes.datc64") ?? throw new FileNotFoundException("Data/DeepwaterBiomes.datc64"));
        (Size, Cells) = Parse(state, rooms);
    }

    /// <summary><c>0x141f35280</c>: u8 N, N² cells (<c>0x141f350c0</c>: u8 DeepwaterRooms row (<c>0x141f565f0</c>), u8
    /// orientation, u8 k, k × (u32 stat, u32 value), little-endian), then u8 m and m mods (<c>0x142436e50</c>: u16 Mods
    /// HASH16, a compact int per stat). An empty state is the client's own 2 × 2 grid. The mods are not read here: nothing in
    /// generation uses them. A short state or a row past the table throws (the client's reader throws on the first; the
    /// second is a null room it crashes on later).</summary>
    public static (int Size, IReadOnlyList<Cell> Cells) Parse(byte[]? state, RawDat rooms)
    {
        if (state is null || state.Length == 0)
        {
            int Row(string id) => rooms.Find(r => rooms.String(r, 0) == id) is var row and >= 0 ? row : throw new InvalidDataException($"no DeepwaterRooms row {id}");
            return (2, [new(Row("ThermalVentsCross1"), 1, []), new(Row("SandyCross1"), 2, []),
                new(Row("CoralReefCross1"), 0, []), new(Row("CoralForestCross1"), 0, [])]);
        }
        int at = 0;
        byte U8() => at < state.Length ? state[at++] : throw new InvalidDataException("the Deepwater plugin state is too short");
        uint U32()
        {
            if (at + 4 > state.Length) throw new InvalidDataException("the Deepwater plugin state is too short");
            uint v = BitConverter.ToUInt32(state, at);
            at += 4;
            return v;
        }
        int n = U8();
        if (n == 0) throw new InvalidDataException("a Deepwater grid of size 0");
        var cells = new List<Cell>(n * n);
        for (int i = 0; i < n * n; i++)
        {
            int room = U8();
            if (room >= rooms.Count) throw new InvalidDataException($"cell {i}: DeepwaterRooms row {room} does not exist");
            byte orientation = U8();
            int k = U8();
            var stats = new List<(uint, uint)>(k);
            for (int s = 0; s < k; s++) stats.Add((U32(), U32()));
            cells.Add(new Cell(room, orientation, stats));
        }
        U8();
        return (n, cells);
    }

    /// <summary>A DeepwaterRooms row's connection mask (+0x10, DeepwaterConnectionStrings 1/2/4/8) turned by the cell's
    /// orientation as <c>0x141f34ee0</c> does: rotated right by the orientation in four bits. Bit 4 opens to the cell at
    /// x + 1, bit 2 to the cell at y + 1.</summary>
    public static int Connections(int mask, int orientation)
    {
        int o = orientation & 3;
        return ((mask << (4 - o)) | (mask >> o)) & 15;
    }

    int Mask(int cell) => Connections(rooms.I32(Cells[cell].Room, 0x10), Cells[cell].Orientation);

    /// <summary>A room's world area: its own (+0x3c, empty in every row) else its biome's (+0x28 → DeepwaterBiomes +8).</summary>
    int WorldArea(int room) => rooms.Ref(room, 0x3c) is var own and >= 0 ? own : biomes.Ref(rooms.Ref(room, 0x28), 8);

    /// <summary><c>0x141f32870</c>: the main record is N × N room units.</summary>
    internal (int W, int H) SizeOf(string graphPath, int w, int h) =>
        string.Equals(graphPath, mainGraph, StringComparison.OrdinalIgnoreCase) ? (Size, Size) : (w, h);

    /// <summary><c>0x141f32a70</c> (from <c>0x142043150</c> once a record's graph is read; only for records whose world area
    /// is of type 52, so neither the hub's nor its interior's). Record 0: N × N "graph" nodes at (23x, 23y), each its room's
    /// world area's topology (<c>Next(count)</c>, one each) with descriptions "1", "1". Records 2 … N² + 1 (cell r − 2,
    /// added by those nodes in order): one node, the room's .arm tag at the cell's orientation; cell 0 also "default" and
    /// "killzone=3" (the player's start).</summary>
    internal void Prepare(TileGraph graph, int record, LevelRandom rng)
    {
        if (record == 0)
        {
            graph.Width = Size; graph.Height = Size;
            graph.Nodes.Clear(); graph.Edges.Clear(); graph.Links.Clear();
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int area = WorldArea(Cells[Size * y + x].Room);
                    var tops = areaTopologies(area) is { Count: > 0 } t ? t : throw new InvalidOperationException($"no topology for the Deepwater cell's world area #{area}");
                    var top = tops[(int)rng.Next((uint)tops.Count)];
                    graph.Nodes.Add(new GraphNode { X = 23 * x, Y = 23 * y, Tag = "graph", Orientation = 0, Descriptions = [top.Graph, "1", "1"] });
                }
            return;
        }
        int cell = record - 2;
        if (cell < 0 || cell >= Cells.Count) return;
        var c = Cells[cell];
        string arm = rooms.String(c.Room, 8);
        var room = files.Room(arm) ?? throw new FileNotFoundException(arm);
        graph.Width = 1; graph.Height = 1;
        graph.Nodes.Clear(); graph.Edges.Clear(); graph.Links.Clear();
        graph.Nodes.Add(new GraphNode { X = 0, Y = 0, Tag = room.Tag, Orientation = c.Orientation,
            Descriptions = cell == 0 ? ["default", "killzone=3"] : [] });
    }

    /// <summary>A record's rectangle (+0x40) and master (+0x50), as the join reads them.</summary>
    internal readonly record struct CellRecord(int X, int Y, int W, int H, string MasterPath, Dictionary<string, string> Master);

    /// <summary><c>0x141f34ee0</c>, after the layout: the map's ground check off (+0x10), then per cell (y, then x) an open
    /// side towards x + 1 joins the 3 × 3 tiles along the record's right edge (<c>0x141f33de0</c>) and one towards y + 1
    /// those along its bottom edge (<c>0x141f34900</c>). Only the lower cell's mask is read. Both re-picks use that
    /// cell's master.</summary>
    internal void Join(TileGrid grid, LevelRandom rng, IReadOnlyList<CellRecord> records)
    {
        grid.CheckGrounds = false;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int i = Size * y + x;
                var r = records[i + 2];
                var seam = new Seam(this, grid, rng, r);
                if (x + 1 < Size && (Mask(i) & 4) != 0) seam.Across(Math.Max(r.X + r.W, r.X) - 3, r.Y, Math.Max(r.Y + r.H, r.Y));
                if (y + 1 < Size && (Mask(i) & 2) != 0) seam.Down(Math.Max(r.Y + r.H, r.Y), r.X, Math.Max(r.X + r.W, r.X));
            }
    }

    readonly Dictionary<string, (TileStore Tiles, Dictionary<string, List<(int Weight, string Path, int Orientation)>> Fills, EdgeCombiner? Combiner)> _masters = new(StringComparer.OrdinalIgnoreCase);

    sealed class Seam
    {
        const byte None = 9; // an edge point of a 3-tile side: 9 is none, 4 the middle
        readonly DeepwaterGeneration plugin;
        readonly TerrainCache files;
        readonly TileGrid grid;
        readonly LevelRandom rng;
        readonly TileStore tiles;
        readonly Dictionary<string, List<(int Weight, string Path, int Orientation)>> fills;
        readonly EdgeCombiner? combiner;

        public Seam(DeepwaterGeneration plugin, TileGrid grid, LevelRandom rng, CellRecord record)
        {
            this.plugin = plugin;
            files = plugin.files;
            this.grid = grid;
            this.rng = rng;
            if (!plugin._masters.TryGetValue(record.MasterPath, out var m))
            {
                string Resolve(string key) => TerrainText.Resolve(record.MasterPath, record.Master.GetValueOrDefault(key) ?? throw new InvalidDataException($"{record.MasterPath}: no {key}"));
                m = (new TileStore(files, files.TileList(Resolve("TileSet"))), files.FillTiles(Resolve("FillTiles")),
                    record.Master.TryGetValue("EdgeCombination", out var ecf) ? EdgeCombiner.Parse(files.Text(TerrainText.Resolve(record.MasterPath, ecf))!, record.MasterPath) : null);
                plugin._masters[record.MasterPath] = m;
            }
            (tiles, fills, combiner) = m;
        }

        /// <summary>The tile's key as the tile data has it (+0x40, grounds left out of keys included), turned by
        /// <c>0x141d83b10</c> with the entry's orientation (+0x36).</summary>
        static TileKey KeyOf(TileEntry e)
        {
            var k = e.Tile.Key.Clone();
            e.Tile.KeyGrounds.CopyTo(k.Grounds, 0);
            return k.Rotate(e.Orientation);
        }

        static bool ThreeByThree(TileEntry? e) => e is { Tile: { Width: 3, Height: 3 } };

        static byte Point(byte a) => a == None ? None : (byte)4;

        /// <summary><c>0x141f33de0</c>: down the seam between x and x + 3, every 3 entries: when both are 3 × 3 tiles, the
        /// left tile's left side runs through both (its top and bottom merged into the right tile's, its own cleared, its
        /// right side and corners taken from its left), and both are picked again.</summary>
        public void Across(int x, int y0, int y1)
        {
            for (int y = y0; y < y1; y += 3)
            {
                var a = grid[x, y];
                var b = grid[x + 3, y];
                if (!ThreeByThree(a) || !ThreeByThree(b)) continue;
                TileKey ka = KeyOf(a!.Value), kb = KeyOf(b!.Value);
                Merge(ka, kb, 2);
                Merge(ka, kb, 0);
                kb.Edges[3] = ka.Edges[3]; kb.PointA[3] = Point(ka.PointA[3]); kb.PointB[3] = ka.PointB[3];
                kb.Grounds[3] = ka.Grounds[3]; kb.Grounds[0] = ka.Grounds[0];
                kb.Heights[3] = ka.Heights[3]; kb.Heights[0] = ka.Heights[0];
                ka.Edges[2] = null; ka.PointA[2] = ka.PointB[2] = None;
                ka.Edges[0] = null; ka.PointA[0] = ka.PointB[0] = None;
                ka.Edges[1] = ka.Edges[3]; ka.PointA[1] = Point(ka.PointA[3]); ka.PointB[1] = ka.PointB[3];
                ka.Grounds[2] = ka.Grounds[3]; ka.Grounds[1] = ka.Grounds[0];
                ka.Heights[2] = ka.Heights[3]; ka.Heights[1] = ka.Heights[0];
                Pick(x, y, ka, a.Value);
                Pick(x + 3, y, kb, b.Value);
            }
        }

        /// <summary><c>0x141f34900</c>: along the seam between rows y − 3 and y. The ends are trimmed first: from the left
        /// (<c>0x141f34200</c>, side 1) and from the right (side 3) to the first column where both tiles are plain fills
        /// (<c>0x141f33260</c>) or neither has an edge on the far side. If more than one column is left, each pair of 3 × 3
        /// tiles is joined: the lower tile's bottom side runs through both (its sides merged into the upper tile's), and
        /// both are picked again, the lower first.</summary>
        public void Down(int y, int x0, int x1)
        {
            if (x0 < x1)
                for (; x0 < x1; x0 += 3)
                    if (End(x0, y, 1)) break;
            if (x0 < x1)
            {
                int before;
                bool stop;
                do
                {
                    before = x1;
                    x1 -= 3;
                    stop = End(x1, y, 3);
                } while (!stop && x0 < x1);
                if (stop) x1 = before;
            }
            if (x0 + 3 >= x1) return;
            for (int x = x0; x < x1; x += 3)
            {
                var a = grid[x, y];
                var b = grid[x, y - 3];
                if (!ThreeByThree(a) || !ThreeByThree(b)) continue;
                TileKey ka = KeyOf(a!.Value), kb = KeyOf(b!.Value);
                Merge(ka, kb, 3);
                Merge(ka, kb, 1);
                kb.Edges[2] = ka.Edges[2]; kb.PointA[2] = Point(ka.PointA[2]); kb.PointB[2] = ka.PointB[2];
                kb.Grounds[3] = ka.Grounds[3]; kb.Grounds[2] = ka.Grounds[2];
                kb.Heights[3] = ka.Heights[3]; kb.Heights[2] = ka.Heights[2];
                ka.Edges[3] = null; ka.PointA[3] = ka.PointB[3] = None;
                ka.Edges[1] = null; ka.PointA[1] = ka.PointB[1] = None;
                ka.Edges[0] = ka.Edges[2]; ka.PointA[0] = Point(ka.PointA[2]); ka.PointB[0] = ka.PointB[2];
                ka.Grounds[0] = ka.Grounds[3]; ka.Grounds[1] = ka.Grounds[2];
                ka.Heights[0] = ka.Heights[3]; ka.Heights[1] = ka.Heights[2];
                Pick(x, y, ka, a.Value);
                Pick(x, y - 3, kb, b.Value);
            }
        }

        /// <summary><c>0x141f34200</c>: whether the trim stops at column x. Two equal edges on <paramref name="side"/> at a
        /// trimmed end go through the master's EdgeCombination with no null check; no Deepwater master has one, so the
        /// client crashes and such a level is never played. The re-pick that follows with a combination (candidates only,
        /// the undo list, <c>0x141f34900</c>'s re-picked shifts) is not ported, because no master that uses this plugin
        /// can reach it.</summary>
        bool End(int x, int y, int side)
        {
            if (PlainFill(grid[x, y]) && PlainFill(grid[x, y - 3])) return true;
            int far = (side - 2) & 3;
            if (grid[x, y] is not { } lower || lower.Tile.Key.Rotate(lower.Orientation).Edges[far] is null)
                if (grid[x, y - 3] is not { } upper || upper.Tile.Key.Rotate(upper.Orientation).Edges[far] is null) return true;
            var a = grid[x, y];
            var b = grid[x, y - 3];
            if (!ThreeByThree(a) || !ThreeByThree(b)) return false;
            string? ea = a!.Value.Tile.Key.Rotate(a.Value.Orientation).Edges[side], eb = b!.Value.Tile.Key.Rotate(b.Value.Orientation).Edges[side];
            if (ea is not null && string.Equals(ea, eb, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Deepwater seam at ({x}, {y}): equal edges at a seam end; the client reads the master's missing EdgeCombination there (0x141f34200 → 0x1401297c0) and crashes");
            return false;
        }

        /// <summary><c>0x141f33260</c>: an entry whose tile has no tag, no edge but wildcards and one ground (its own key).</summary>
        bool PlainFill(TileEntry? e) => e is { } t && t.Tile.Tag.Length == 0 && Seams.PlainFill(files, t.Tile);

        /// <summary><c>0x141f33c90</c>: side <paramref name="side"/> of b takes a's edge when b has none, else the master's
        /// EdgeCombination of the two (none without one).</summary>
        void Merge(TileKey a, TileKey b, int side)
        {
            if (a.Edges[side] is not { } edge) return;
            if (b.Edges[side] is null)
            {
                b.Edges[side] = edge; b.PointA[side] = Point(a.PointA[side]); b.PointB[side] = a.PointB[side];
                return;
            }
            b.Edges[side] = combiner?.Combine(b.Edges[side], edge, 0);
            b.PointA[side] = b.Edges[side] is null ? None : (byte)4;
            b.PointB[side] = None;
        }

        /// <summary><c>0x141f33560</c>: a tile for the joined key at (x, y). An untagged tile whose key has no edge but
        /// wildcards and one ground takes a 3 × 3 fill tile of that ground (<c>Next(8)</c> orientation, the entry's height),
        /// else nine 1 × 1 ones (each <c>Next(8)</c>, corner 0's height). Otherwise the TileSet's tiles of the old tile's
        /// tag with the key (<c>0x141d85600</c>; with none, again with the corners whose ground has no flag 1 at height 0),
        /// weighted. When nothing fits, the grounds left out of keys go and it tries again; without one it fails as the
        /// client does.</summary>
        void Pick(int x, int y, TileKey key, TileEntry original)
        {
            if (original.Tile.Tag.Length == 0
                && key.Edges.All(e => e is null || files.Edge(e)?.Wildcard == true)
                && key.Grounds.All(g => string.Equals(g, key.Grounds[0], StringComparison.OrdinalIgnoreCase)))
            {
                if (Fill(key.Grounds[0], 3) is { } big)
                {
                    grid.Place(big, rng.Next(8), x, y, original.HeightOffset, true);
                    return;
                }
                if (Fill(key.Grounds[0], 1) is { } small)
                {
                    for (int dx = 0; dx < 3; dx++)
                        for (int dy = 0; dy < 3; dy++)
                            grid.Place(small, rng.Next(8), x + dx, y + dy, key.Heights[0], true);
                    return;
                }
            }
            else
            {
                var choices = tiles.Candidates(original.Tile.Tag, key);
                if (choices.Count == 0 && key.Grounds.Any(g => g is not null && ((files.Ground(g)?.Flags ?? 0) & 1) == 0))
                {
                    var flat = key.Clone();
                    for (int c = 0; c < 4; c++)
                        if (flat.Grounds[c] is { } g && ((files.Ground(g)?.Flags ?? 0) & 1) == 0) flat.Heights[c] = 0;
                    choices = tiles.Candidates(original.Tile.Tag, flat);
                }
                if (choices.Count > 0)
                {
                    int roll = rng.Next(choices.Sum(c => c.Weight));
                    var chosen = choices[^1];
                    foreach (var c in choices) { if (roll < c.Weight) { chosen = c; break; } roll -= c.Weight; }
                    grid.Place(chosen.Tile, chosen.Orientation < 8 ? chosen.Orientation : rng.Next(8), x, y, original.HeightOffset, true);
                    return;
                }
            }
            if (!key.Grounds.Any(g => files.Ground(g) is { IgnoredInKeys: true }))
                throw new InvalidOperationException($"Cannot find matching tile key to connect Deepwater cell at ({x}, {y}): {key}");
            var rest = key.Clone();
            for (int c = 0; c < 4; c++)
                if (files.Ground(rest.Grounds[c]) is { IgnoredInKeys: true }) rest.Grounds[c] = null;
            Pick(x, y, rest, original);
        }

        /// <summary><c>0x141f33340</c>: the master's fill tiles (every list of the FillTiles object, <c>0x141ff6ba0</c>) that
        /// are size × size with the ground on their own corner 0 (<c>0x141f5f530</c>), weighted: one <c>Next(total)</c>,
        /// drawn even when none is. For no ground or a ground with flag 4 a tile also needs bits 0x40 and 0x80 of its flags
        /// (tile data +0x16c) and no bit 8 in its first slot's F (tile data +0x90 → +0x30). Rooms in the lists never count.
        /// The client takes the "default" list first (FillTiles +0x100, its rooms +0x120), then the named lists of its
        /// hash maps (<c>0x141ff6ba0</c>). In the Deepwater .gft files only "default" and "BeachMiddleSand" share a
        /// request (no ground, 1 × 1), and "default" comes first in both, so file order is the client's.</summary>
        TileDef? Fill(string? ground, int size)
        {
            bool strict = ground is null || ((files.Ground(ground)?.Flags ?? 0) & 4) != 0;
            var found = new List<(TileDef Tile, int Weight)>();
            foreach (var list in fills.Values)
                foreach (var (weight, path, _) in list)
                    if (weight != 0 && path.EndsWith(".tdt", StringComparison.OrdinalIgnoreCase) && files.Tile(path) is { } t
                        && t.Width == size && t.Height == size && string.Equals(t.KeyGrounds[0], ground, StringComparison.OrdinalIgnoreCase)
                        && (!strict || (files.TileFlags(path) & 0xc0) == 0xc0 && (FirstSlot(path) & 8) == 0))
                        found.Add((t, weight));
            int total = found.Sum(f => f.Weight);
            int roll = rng.Next(total);
            if (total == 0) return null;
            foreach (var (tile, weight) in found)
            {
                if (roll < weight) return tile;
                roll -= weight;
            }
            return found[^1].Tile;
        }

        int FirstSlot(string path) => files.Walk(path) is { SubTiles.Length: > 0 } w ? w.SubTiles[0].F
            : throw new NotSupportedException($"Deepwater seam: {path} has no slot record (0x141f5f530 reads tile data +0x90)");
    }
}
