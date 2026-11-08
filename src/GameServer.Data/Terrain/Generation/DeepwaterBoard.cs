namespace GameServer.Data.Terrain.Generation;

/// <summary>The DeepwaterPlugin's voyage board (0x1d's TerrainPluginState 11572: u8 N, then N × N cells of {u8
/// DeepwaterRooms row, u8 rotation 0-3, stats}; the layout the server corpus decoded). The plugin's generation hook
/// (<c>0x141f32a70</c>, the terrain plugin's vtable +0x30, called by <c>0x142043150</c> right after the graph load for
/// a record whose WorldAreas row has +0x1e4 = 52: the encounter areas and the four biome areas) replaces the loaded
/// graph: record 0's <c>Deepwater_01.dgr</c> becomes an N × N graph with one "graph" node per cell (y outer, x inner)
/// at (23x, 23y), orientation 0, descriptions [the cell's biome area's topology, drawn by <c>Next(count)</c> at
/// <c>0x141f32b8d</c> (one topology per biome, so no word), "1", "1"]; each cell's <c>Biome_*.dgr</c> record becomes a
/// 1 × 1 graph with one node at (0, 0) tagged with the cell room's header name (the .arm tag), oriented by the cell's
/// rotation, descriptions "default" (+ "killzone=3" on cell 0). The biome is the room's DeepwaterRooms +0x28 →
/// DeepwaterBiomes +8 → WorldAreas (Sandy, CoralForest, CoralReef, ThermalVents). The plugin also queues the hub
/// (DeepwaterHub) as a sub-area. An N × N board (N &gt; 1) ends with the plugin's post-level hook that tiles the
/// connections between the cells (<c>0x141f33560</c>; not ported).</summary>
public sealed class DeepwaterBoard(int n, IReadOnlyList<(string RoomId, int Rotation)> cells)
{
    public int N => n;
    int _cell;

    public static bool IsEncounterGraph(string graphPath)
    {
        string p = graphPath.Replace('\\', '/');
        return p.Contains("/Leagues/Deepwater/Graphs/", StringComparison.OrdinalIgnoreCase) && !IsBiomeGraph(p);
    }

    public static bool IsBiomeGraph(string graphPath) =>
        graphPath.Replace('\\', '/').Contains("/Leagues/Deepwater/Graphs/Biome_", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<(string RoomId, int Rotation)> Cells => cells;

    /// <summary>The board as the DeepwaterPlugin's state bytes (0x1d's TerrainPluginState 11572, the plugin's constructor
    /// <c>0x141f35280</c>): u8 N, then N × N cells of {u8 DeepwaterRooms row, u8 rotation, u8 stat count = 0}, then
    /// u8 mod count = 0.</summary>
    public byte[] Encode(Func<string, byte[]?> read)
    {
        var rooms = new RawDat(read("Data/DeepwaterRooms.datc64") ?? throw new FileNotFoundException("Data/DeepwaterRooms.datc64"));
        var bytes = new List<byte> { (byte)n };
        foreach (var (roomId, rotation) in cells)
        {
            int row = rooms.Find(r => rooms.String(r, 0) == roomId);
            if (row < 0) throw new InvalidDataException($"no DeepwaterRooms row {roomId}");
            bytes.Add((byte)row);
            bytes.Add((byte)rotation);
            bytes.Add(0);
        }
        bytes.Add(0);
        return [.. bytes];
    }

    /// <summary>The rooms the server put on a 1 × 1 mission board (every one of the server corpus's 133 missions:
    /// the three shallow biomes' plain rooms and some of their feature rooms, with any rotation).</summary>
    public static readonly string[] MissionRooms =
    [
        "SandyEnd1", "SandyEnd2", "SandyEnd3", "SandyEnd4", "SandyStraight1", "SandyStraight3", "SandyStraight4", "SandyCorner1", "SandyCorner2",
        "SandyCorner3", "SandyCorner4", "SandyT1", "SandyT2", "SandyT3", "SandyT4", "SandyCross1", "SandyCross2", "SandyCross3", "SandyAnchorField2",
        "SandyBathyspheres2", "SandyCrashedShip1", "CoralForestEnd1", "CoralForestEnd2", "CoralForestStraight1", "CoralForestStraight2",
        "CoralForestCorner1", "CoralForestT1", "CoralForestT2", "CoralForestCross1", "CoralForestCross2", "CoralForestCross3", "CoralForestAbyssalPit2",
        "CoralForestStarfishPillars1", "CoralForestStarfishPillars2", "CoralForestVaalRuins1", "CoralForestVaalRuins2", "CoralReefEnd1", "CoralReefEnd2",
        "CoralReefStraight1", "CoralReefStraight2", "CoralReefCorner1", "CoralReefCorner2", "CoralReefT1", "CoralReefT2", "CoralReefCross1",
        "CoralReefCross2", "CoralReefCross3", "CoralReefBrineKingShrines2", "CoralReefDeepSeaDiver1", "CoralReefKaruiTotem2",
    ];

    /// <summary>A 1 × 1 mission board the server would send for a Deepwater encounter: one of <see cref="MissionRooms"/>
    /// and a rotation, drawn from the instance's seed (the server's own choice; the client takes whatever it is sent).</summary>
    public static DeepwaterBoard Mission(uint seed)
    {
        uint h = unchecked(seed * 0x9e3779b1u + 0x7f4a7c15u);
        h ^= h >> 15; h = unchecked(h * 0x2c1b3c6du); h ^= h >> 12;
        return new DeepwaterBoard(1, [(MissionRooms[(int)(h % (uint)MissionRooms.Length)], (int)((h >> 8) & 3))]);
    }

    (RawDat Rooms, RawDat Biomes) Tables(Func<string, byte[]?> read) =>
        (new RawDat(read("Data/DeepwaterRooms.datc64") ?? throw new FileNotFoundException("Data/DeepwaterRooms.datc64")),
         new RawDat(read("Data/DeepwaterBiomes.datc64") ?? throw new FileNotFoundException("Data/DeepwaterBiomes.datc64")));

    /// <summary>Record 0: the board graph in place of the loaded one.</summary>
    public void Board(TileGraph graph, Func<string, byte[]?> read, Func<int, IReadOnlyList<TopologyInfo>?> topologiesOf, int variant,
        LevelRandom rng, List<string> notes)
    {
        var (rooms, biomes) = Tables(read);
        graph.Width = graph.Height = n;
        graph.Nodes = [];
        graph.Edges = [];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int cell = y * n + x;
                var (roomId, _) = cells[cell];
                int row = rooms.Find(r => rooms.String(r, 0) == roomId);
                if (row < 0) throw new InvalidDataException($"no DeepwaterRooms row {roomId}");
                int biome = rooms.I32(row, 0x28);
                int areaRow = biomes.Ref8(biome, 8);
                var topologies = topologiesOf(areaRow) ?? throw new InvalidDataException($"DeepwaterBiomes {biome}: no world area");
                var choices = LevelGenerator.Choices(topologies, variant, $"biome {biomes.String(biome, 0)}");
                var topology = choices[(int)rng.Next((uint)choices.Count)];
                graph.Nodes.Add(new GraphNode { X = 23 * x, Y = 23 * y, Tag = "graph", Orientation = 0, Descriptions = [topology.Graph, "1", "1"] });
                notes.Add($"deepwater board cell {cell}: {roomId} ({biomes.String(biome, 0)}) -> {topology.Graph}");
            }
    }

    /// <summary>A board cell's record on the shared map (its view rectangle, its master's tile store and fill tiles).</summary>
    internal sealed record CellRecord(int X, int Y, int W, int H, TileStore Store, Dictionary<string, string> Master, string MasterPath);

    /// <summary>The plugin's post-level hook (<c>0x141f34ee0</c>, after every record's pipeline call and before the AO
    /// placements and the outer buffers): rows outer, columns inner over the board, each cell's DeepwaterRooms door
    /// bits (+0x10) turned by its rotation; bit 4 (east) with a cell to the right tiles the seam column
    /// (<c>0x141f33de0</c>: the last 3 × 3 column of this cell and the first of the next, every row block), bit 2
    /// (south) with a cell below the seam row (<c>0x141f34900</c>: a door scan from the left and from the right,
    /// <c>0x141f34200</c>, then the columns strictly between the two doors get the straight-through keys, or the saved
    /// originals go back). Each seam tile is re-picked by <c>0x141f33560</c>: a plain key from the fill tiles of its
    /// ground (3 × 3, else 1 × 1 nine times), any other from the tile store under the tile's tag. Ported from the
    /// corpus's dungeon_sim (its replay of the two 3 × 3 voyages matched draw for draw).</summary>
    internal void Connections(TileGrid grid, TerrainCache files, IReadOnlyList<CellRecord> records, LevelRandom rng, List<string> notes)
    {
        if (n <= 1) return;
        var (rooms, _) = Tables(files.Raw);
        for (int row = 0; row < n; row++)
            for (int col = 0; col < n; col++)
            {
                int idx = col + row * n;
                if (idx >= records.Count) return;
                var (roomId, rotation) = cells[idx];
                int r = rooms.Find(x => rooms.String(x, 0) == roomId);
                int m = r < 0 ? 0 : rooms.I32(r, 0x10);
                int rot = rotation & 3;
                m = ((m >> rot) | (m << (4 - rot))) & 0xf;
                var seams = new Seams(grid, files, records[idx], rng, notes);
                if (col + 1 < n && (m & 4) != 0) seams.East();
                if (row + 1 < n && (m & 2) != 0) seams.South();
            }
    }

    sealed class Seams(TileGrid grid, TerrainCache files, CellRecord cell, LevelRandom rng, List<string> notes)
    {
        static readonly int[][] DoorCorners = [[4, 1, 4, 0], [1, 4, 2, 4], [4, 2, 4, 3], [0, 4, 3, 4]];

        /// <summary>The placed tile's key as the client's tile data holds it (tile data +0x40: the grounds left out of
        /// keys included, which <see cref="TileDef.Key"/> drops for lookups), turned by its orientation.</summary>
        static TileKey RawKey(TileDef t)
        {
            var k = t.Key.Clone();
            for (int i = 0; i < 4; i++) k.Grounds[i] = t.KeyGrounds[i];
            return k;
        }

        TileKey KeyOf(TileEntry e) => RawKey(e.Tile).Rotate(e.Orientation);
        bool Is3x3(TileEntry? e) => e is { } t && t.Tile.Width == 3 && t.Tile.Height == 3;
        TileEntry? At(int x, int y) => grid[x, y];
        int Flags(string? ground) => ground is null ? 0 : files.Ground(ground)?.Flags ?? 0;

        /// <summary><c>0x141f33260</c>: a tile without a tag, without a real edge in its own key, four equal corners.</summary>
        bool IsPlain(int x, int y)
        {
            if (At(x, y) is not { } e || e.Tile.Tag.Length != 0) return false;
            var k = RawKey(e.Tile);
            if (k.Edges.Any(d => d is not null)) return false;
            return k.Grounds.All(g => string.Equals(g, k.Grounds[0], StringComparison.OrdinalIgnoreCase));
        }

        /// <summary><c>0x141f33c90</c>: k1's edge on the side folded into k2 (the master's edge combination map is empty
        /// in every Deepwater master: both set gives none).</summary>
        static void Combine(TileKey k1, TileKey k2, int side)
        {
            if (k1.Edges[side] is null) return;
            if (k2.Edges[side] is not null)
            {
                k2.Edges[side] = null;
                k2.PointA[side] = k2.PointB[side] = 9;
            }
            else
            {
                k2.Edges[side] = k1.Edges[side];
                k2.PointA[side] = (byte)(k1.PointA[side] == 9 ? 9 : 4);
                k2.PointB[side] = k1.PointB[side];
            }
        }

        /// <summary><c>0x141f331f0</c>: edge slot dst = slot src, point A 4 (9 when src's is 9), point B src's.</summary>
        static void Move(TileKey k, int src, int dst)
        {
            if (dst != src) k.Edges[dst] = k.Edges[src];
            k.PointA[dst] = (byte)(k.PointA[src] == 9 ? 9 : 4);
            k.PointB[dst] = k.PointB[src];
        }

        static void Clear(TileKey k, int side)
        {
            k.Edges[side] = null;
            k.PointA[side] = k.PointB[side] = 9;
        }

        /// <summary><c>0x141f33de0</c>: every row block of the seam column pair.</summary>
        public void East()
        {
            int x0 = cell.X + cell.W - 3;
            for (int y = cell.Y; y < cell.Y + cell.H; y += 3)
            {
                var l = At(x0, y);
                var r = At(x0 + 3, y);
                if (!Is3x3(l) || !Is3x3(r)) continue;
                var kl = KeyOf(l!.Value);
                var kr = KeyOf(r!.Value);
                Combine(kl, kr, 2);
                Combine(kl, kr, 0);
                // the right key's west side takes the left key's west side; the left key becomes straight-through
                kr.Edges[3] = kl.Edges[3]; kr.PointA[3] = (byte)(kl.PointA[3] == 9 ? 9 : 4); kr.PointB[3] = kl.PointB[3];
                kr.Grounds[3] = kl.Grounds[3]; kr.Grounds[0] = kl.Grounds[0];
                kr.Heights[3] = kl.Heights[3]; kr.Heights[0] = kl.Heights[0];
                Clear(kl, 2);
                Clear(kl, 0);
                kl.Edges[1] = kl.Edges[3]; kl.PointA[1] = (byte)(kl.PointA[3] == 9 ? 9 : 4); kl.PointB[1] = kl.PointB[3];
                kl.Grounds[2] = kl.Grounds[3]; kl.Grounds[1] = kl.Grounds[0];
                kl.Heights[2] = kl.Heights[3]; kl.Heights[1] = kl.Heights[0];
                Connect(x0, y, kl, l.Value, false);
                Connect(x0 + 3, y, kr, r.Value, false);
            }
        }

        /// <summary><c>0x141f34900</c>: the seam row below the cell.</summary>
        public void South()
        {
            int y1 = cell.Y + cell.H, x0 = cell.X, x1 = cell.X + cell.W;
            var saved = new List<(int X, int Y, TileEntry Entry)>();
            bool flag = false;
            int v6 = x0;
            if (x0 < x1)
            {
                while (true)
                {
                    if (DoorScan(v6, y1, 1, ref flag, saved)) break;
                    v6 += 3;
                    if (v6 >= x1) break;
                }
                if (flag) v6 += 3;
            }
            flag = false;
            int v5 = x1;
            if (v6 < v5)
            {
                bool v12;
                int v10;
                while (true)
                {
                    v10 = v5; v5 -= 3;
                    bool res = DoorScan(v5, y1, 3, ref flag, saved);
                    v12 = !res;
                    if (res) break;
                    if (v6 >= v5) { v12 = true; break; }
                }
                if (!v12) v5 = v10;
                if (flag) v5 -= 3;
            }
            if (v6 + 3 < v5)
                for (int colX = v6; colX < v5; colX += 3) Middle(colX, y1);
            else
                foreach (var (x, y, e) in saved) grid.Place(e.Tile, e.Orientation, x, y, e.HeightOffset);
        }

        /// <summary><c>0x141f34200</c>: the column where both tiles carry the same real edge on their facing side (the
        /// room connection edge = the door): the originals are saved, the edge moved to the seam side and both re-picked.</summary>
        bool DoorScan(int x, int y, int d, ref bool flag, List<(int, int, TileEntry)> saved)
        {
            int y2 = y - 3;
            if (IsPlain(x, y) && IsPlain(x, y2)) return true;
            int v15 = (d - 2) & 3;
            var c1 = At(x, y);
            var c2 = At(x, y2);
            string? RawEdge(TileEntry e) => KeyOf(e).Edges[v15];
            bool ok = c1 is { } e1 && RawEdge(e1) is not null;
            if (!ok && (c2 is not { } e2 || RawEdge(e2) is null)) return true;
            if (!Is3x3(c1) || !Is3x3(c2)) return false;
            var k1 = KeyOf(c1!.Value);
            var k2 = KeyOf(c2!.Value);
            string? e = k1.Edges[d];
            if (e is null || !string.Equals(k2.Edges[d], e, StringComparison.OrdinalIgnoreCase)) return false;
            saved.Add((x, y, c1.Value));
            saved.Add((x, y2, c2.Value));
            Move(k1, d, 0); Clear(k1, d);
            Move(k2, d, 2); Clear(k2, d);
            var t = DoorCorners[d];
            k1.Grounds[t[0]] = k1.Grounds[t[2]];
            k2.Grounds[t[2]] = k1.Grounds[t[0]];
            k1.Heights[t[0]] = k1.Heights[t[2]];
            k2.Heights[t[2]] = k1.Heights[t[0]];
            Connect(x, y, k1, c1.Value, true);
            Connect(x, y2, k2, c2.Value, true);
            flag = true;
            return true;
        }

        void Middle(int colX, int y1)
        {
            var c1 = At(colX, y1);
            var c2 = At(colX, y1 - 3);
            if (!Is3x3(c1) || !Is3x3(c2)) return;
            var k1 = KeyOf(c1!.Value);   // south
            var k2 = KeyOf(c2!.Value);   // north
            Combine(k1, k2, 3);
            Combine(k1, k2, 1);
            k2.Edges[2] = k1.Edges[2]; k2.PointA[2] = (byte)(k1.PointA[2] == 9 ? 9 : 4); k2.PointB[2] = k1.PointB[2];
            k2.Grounds[3] = k1.Grounds[3]; k2.Grounds[2] = k1.Grounds[2];
            k2.Heights[3] = k1.Heights[3]; k2.Heights[2] = k1.Heights[2];
            Clear(k1, 3);
            Clear(k1, 1);
            k1.Edges[0] = k1.Edges[2]; k1.PointA[0] = (byte)(k1.PointA[2] == 9 ? 9 : 4); k1.PointB[0] = k1.PointB[2];
            k1.Grounds[0] = k1.Grounds[3]; k1.Grounds[1] = k1.Grounds[2];
            k1.Heights[0] = k1.Heights[3]; k1.Heights[1] = k1.Heights[2];
            Connect(colX, y1, k1, c1.Value, false);
            Connect(colX, y1 - 3, k2, c2.Value, false);
        }

        /// <summary><c>0x141f33340</c>'s list: the fill tiles of this ground that are mode × mode; a null or flag-4 ground
        /// admits only the tiles whose every sub-tile has walk flags 1 and 2 and whose first lacks 8.</summary>
        List<(TileDef Tile, int Weight)> FillCandidates(int mode, string? ground)
        {
            var fills = cell.Master.TryGetValue("FillTiles", out var ft) ? files.FillTiles(TerrainText.Resolve(cell.MasterPath, ft)) : [];
            bool wild = ground is null || (Flags(ground) & 4) != 0;
            string want = Path.GetFileName(ground ?? "").ToLowerInvariant();
            var groups = new List<List<(int Weight, string Path, int Orientation)>>();
            if (fills.TryGetValue("default", out var def)) groups.Add(def);
            foreach (var (name, list) in fills.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (name != "default") groups.Add(list);
            var outList = new List<(TileDef, int)>();
            foreach (var list in groups)
                foreach (var (weight, path, _) in list)
                {
                    if (path.EndsWith(".arm", StringComparison.OrdinalIgnoreCase)) continue;
                    if (files.Tile(path) is not { } t || t.Width != mode || t.Height != mode) continue;
                    // The tile's own corner ground as the client's key holds it (0x141d84a50 drops the grounds left out of
                    // keys, flag 0x10): a tile whose raw corners name such a ground has none here, so a request for
                    // it finds nothing until the retry clears it (the voyage 387284895's draw #2436).
                    string? own = t.Key.Grounds.FirstOrDefault(g => g is not null && !(files.Ground(g)?.IgnoredInKeys ?? false));
                    if (Path.GetFileName(own ?? "").ToLowerInvariant() != want) continue;
                    if (wild && !WildcardOk(path)) continue;
                    outList.Add((t, weight));
                }
            return outList;
        }

        bool WildcardOk(string path)
        {
            var walk = files.Walk(path);
            if (walk is null || walk.SubTiles.Length == 0) return false;
            return walk.SubTiles.All(s => (s.F & 1) != 0) && walk.SubTiles.All(s => (s.F & 2) != 0) && (walk.SubTiles[0].F & 8) == 0;
        }

        static T Pick<T>(List<(T Item, int Weight)> list, uint r)
        {
            int acc = 0;
            foreach (var (item, w) in list)
            {
                if (r < acc + w) return item;
                acc += w;
            }
            return list[^1].Item;
        }

        /// <summary><c>0x141f33560</c>: the re-pick of one seam tile.</summary>
        void Connect(int x, int y, TileKey key, TileEntry c, bool forced, int depth = 0)
        {
            short h = c.HeightOffset;
            bool tagged = c.Tile.Tag.Length != 0;
            if (!forced && !tagged && key.Edges.All(e => e is null) &&
                key.Grounds.All(g => string.Equals(g, key.Grounds[0], StringComparison.OrdinalIgnoreCase)))
            {
                var list = FillCandidates(3, key.Grounds[0]);
                uint r = rng.Next((uint)list.Sum(e => e.Weight));
                if (list.Count != 0)
                {
                    var tile = Pick(list, r);
                    int o = (int)rng.Next(8u);
                    grid.Place(tile, o, x, y, h);
                    return;
                }
                list = FillCandidates(1, key.Grounds[0]);
                r = rng.Next((uint)list.Sum(e => e.Weight));
                if (list.Count != 0)
                {
                    var tile = Pick(list, r);
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j < 3; j++)
                        {
                            int o = (int)rng.Next(8u);
                            grid.Place(tile, o, x + i, y + j, key.Heights[0]);   // the key's first corner height, not the cell's
                        }
                    return;
                }
                Retry(x, y, key, c, forced, depth);
                return;
            }
            string tag = c.Tile.Tag;
            var cands = cell.Store.Candidates(tag, key);
            if (cands.Count == 0)
            {
                Retry(x, y, key, c, forced, depth);
                return;
            }
            uint pick = rng.Next((uint)cands.Sum(e => e.Weight));
            var chosen = Pick(cands.Select(e => ((e.Tile, e.Orientation), e.Weight)).ToList(), pick);
            int orientation = chosen.Orientation;
            if (orientation == 8) orientation = (int)rng.Next(8u);
            grid.Place(chosen.Tile, orientation, x, y, h);
        }

        /// <summary>Corners whose ground has flag 0x10 are cleared and the pick retried, else the client throws "Cannot
        /// find matching tile key to connect Deepwater cell at".</summary>
        void Retry(int x, int y, TileKey key, TileEntry c, bool forced, int depth)
        {
            if (depth < 4 && key.Grounds.Any(g => g is not null && (Flags(g) & 0x10) != 0))
            {
                var k2 = key.Clone();
                for (int i = 0; i < 4; i++)
                    if (k2.Grounds[i] is { } g && (Flags(g) & 0x10) != 0) k2.Grounds[i] = null;
                Connect(x, y, k2, c, forced, depth + 1);
                return;
            }
            notes.Add($"deepwater seam ({x}, {y}): cannot find a matching tile key - the cell is left as it is");
        }
    }

    /// <summary>The next cell's record: a one-node graph with the cell's room and rotation.</summary>
    public void Cell(TileGraph graph, TerrainCache files, List<string> notes)
    {
        int cell = _cell++;
        if (cell >= cells.Count) throw new InvalidDataException($"deepwater board: record {cell} beyond the {cells.Count} cells");
        var (roomId, rotation) = cells[cell];
        var (rooms, _) = Tables(files.Raw);
        int row = rooms.Find(r => rooms.String(r, 0) == roomId);
        string arm = rooms.String(row, 8);
        var room = files.Room(arm) ?? throw new FileNotFoundException(arm);
        graph.Width = graph.Height = 1;
        graph.Edges = [];
        graph.Nodes = [new GraphNode { X = 0, Y = 0, Tag = room.Tag, Orientation = (byte)rotation, Descriptions = cell == 0 ? ["default", "killzone=3"] : ["default"] }];
        notes.Add($"deepwater cell {cell}: {Path.GetFileName(arm)} (tag '{room.Tag}') rotation {rotation}");
    }
}
