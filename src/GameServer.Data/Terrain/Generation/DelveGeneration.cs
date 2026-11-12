namespace GameServer.Data.Terrain.Generation;

/// <summary>The native Mines graph pass (0x141f2f840, 0x141f2ff30), driven by the area packet's Delve cells.</summary>
public sealed class DelveGeneration
{
    public const string Main = "Metadata/Terrain/Leagues/Delve/Graphs/delve_main.dgr";
    public const string EmptyCellGraph = "Metadata/Terrain/Leagues/Delve/Graphs/_delve_cell_empty.dgr";
    public const string CellGraph = "Metadata/Terrain/Leagues/Delve/Graphs/_delve_cell.dgr";
    const string MineMaster = "Metadata/Terrain/Leagues/Delve/Biomes/Mine/master.tsi";
    readonly byte[] terrain;
    readonly TerrainCache files;
    readonly Dictionary<int, string> injected = [];
    public int Width => terrain[8] & 15;
    public int Height => terrain[8] >> 4;

    public DelveGeneration(TerrainCache files, byte[] terrain)
    {
        this.files = files;
        this.terrain = (byte[])terrain.Clone();
        if (terrain.Length < 10 || terrain[0] != 0x20 || terrain[1] != 0 || Width == 0 || Height == 0
            || terrain.Length != 10 + Width * Height * 5) throw new ArgumentException("Invalid Delve terrain block");
        for (int i = 0; i < Width * Height; i++)
            if ((terrain[11 + 5 * i] & 15) != 0 || terrain[10 + 5 * i] is > 0 and < 7)
                throw new NotSupportedException("The procedural Mines pass needs ordinary encounter cells");
    }

    internal string Master(string graphPath, string fallback) => graphPath == CellGraph ? MineMaster : fallback;
    internal (int W, int H) Size(string graphPath, int w, int h) => graphPath == Main ? (Width, Height) : (w, h);
    internal void Inject(DungeonRoomSet set, int record)
    {
        if (injected.TryGetValue(record, out string? path) && DungeonRoomSet.Parse(files, path, 1, 255) is { } room)
            set.All.Add(room);
    }

    internal void Join(TileGrid grid, LevelRandom rng)
    {
        var master = TerrainText.KeyValues(files.Text(MineMaster)!);
        var tiles = new TileStore(files, files.TileList(TerrainText.Resolve(MineMaster, master["TileSet"])));
        var fills = files.FillTiles(TerrainText.Resolve(MineMaster, master["FillTiles"]));
        var combiner = EdgeCombiner.Parse(files.Text(TerrainText.Resolve(MineMaster, master["EdgeCombination"]))!, MineMaster);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int at = 10 + (y * Width + x) * 5;
                if (x + 1 < Width && (terrain[at + 1] & 0x20) != 0)
                    for (int row = 4 + (Height - y - 1) * 25; row < 4 + (Height - y) * 25; row++) Pair(4 + (x + 1) * 25 - 1, row, 1);
                if (y > 0 && (terrain[at + 1 - Width * 5] & 0x10) != 0)
                    for (int col = 4 + x * 25; col < 4 + (x + 1) * 25; col++) Pair(col, 4 + (Height - y) * 25, 0);
            }
        void Pair(int x, int y, int direction)
        {
            var delta = Orientation.Directions[direction];
            if (grid[x, y] is not { IsOrigin: true } a || grid[x + delta.X, y + delta.Y] is not { IsOrigin: true } b
                || a.Tile.Width != 1 || a.Tile.Height != 1 || b.Tile.Width != 1 || b.Tile.Height != 1)
                throw new NotSupportedException("Delve sleeve joins need two one-tile entries");
            // Work in the horizontal pair's coordinates, then turn the merged keys back for a vertical seam.
            int turn = direction == 1 ? 0 : 1;
            var ka = a.Tile.Key.Rotate(a.Orientation).Rotate(turn);
            var kb = b.Tile.Key.Rotate(b.Orientation).Rotate(turn);
            foreach (int side in new[] { 0, 2 })
                if (ka.Edges[side] is { } edge)
                {
                    if (kb.Edges[side] is null)
                    { kb.Edges[side] = edge; kb.PointA[side] = ka.PointA[side]; kb.PointB[side] = ka.PointB[side]; }
                    else
                    {
                        kb.Edges[side] = combiner.Combine(edge, kb.Edges[side], 0);
                        kb.PointA[side] = kb.Edges[side] is null ? (byte)3 : (byte)1; kb.PointB[side] = 3;
                    }
                }
            CopySide(ka, 3, kb, 3);
            kb.Grounds[0] = ka.Grounds[0]; kb.Grounds[3] = ka.Grounds[3];
            ka.Edges[0] = ka.Edges[2] = null;
            ka.PointA[0] = ka.PointB[0] = ka.PointA[2] = ka.PointB[2] = 3;
            CopySide(ka, 3, ka, 1);
            ka.Grounds[1] = ka.Grounds[0]; ka.Grounds[2] = ka.Grounds[3];
            Pick(x, y, a, ka.Rotate(direction == 1 ? 0 : 3));
            Pick(x + delta.X, y + delta.Y, b, kb.Rotate(direction == 1 ? 0 : 3));
        }
        static void CopySide(TileKey a, int side, TileKey b, int target)
        { b.Edges[target] = a.Edges[side]; b.PointA[target] = a.PointA[side]; b.PointB[target] = a.PointB[side]; }
        void Pick(int x, int y, TileEntry original, TileKey key)
        {
            string ground = files.Ground(key.Grounds[0])?.Name ?? "default";
            var fill = key.Edges.All(e => e is null || files.Edge(e)?.Wildcard == true)
                && key.Grounds.Distinct().Count() == 1 ? fills.GetValueOrDefault(ground) ?? fills.GetValueOrDefault("default") : null;
            if (fill is { Count: > 0 })
            {
                int roll = rng.Next(fill.Sum(f => f.Weight));
                var chosen = fill[^1];
                foreach (var f in fill) { if (roll < f.Weight) { chosen = f; break; } roll -= f.Weight; }
                grid.Place(files.Tile(chosen.Path)!, chosen.Orientation < 8 ? chosen.Orientation : rng.Next(8), x, y, original.HeightOffset, true);
                return;
            }
            var choices = tiles.Candidates(original.Tile.Tag, key);
            if (choices.Count == 0) throw new NotSupportedException($"No native Delve sleeve tile at {x},{y}: {key}");
            int pick = rng.Next(choices.Sum(c => c.Weight));
            var entry = choices[^1];
            foreach (var c in choices) { if (pick < c.Weight) { entry = c; break; } pick -= c.Weight; }
            grid.Place(entry.Tile, entry.Orientation, x, y, original.HeightOffset, true);
        }
    }

    internal void Prepare(TileGraph graph, string path, int record, LevelRandom rng)
    {
        if (path == Main)
        {
            graph.Width = Width; graph.Height = Height;
            graph.Nodes.Clear(); graph.Edges.Clear(); graph.Links.Clear();
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                    graph.Nodes.Add(new GraphNode { X = x * 23 + 11, Y = (Height - y - 1) * 23 + 11,
                        Tag = "graph", Orientation = 0, Descriptions = [(terrain[11 + (y * Width + x) * 5] & 0xf0) == 0 ? EmptyCellGraph : CellGraph, "1", "1"] });
            return;
        }
        if (path != CellGraph) return;
        int cell = record - 2; // primary, copied hub, then the row-major mine cells
        if (cell < 0 || cell >= Width * Height) throw new InvalidOperationException("Invalid Delve cell record");
        int offset = 10 + cell * 5;
        int feature = terrain[offset], connections = terrain[offset + 1] >> 4, portals = terrain[offset + 2];
        bool completed = (terrain[offset + 4] & 1) != 0;
        var centre = graph.Nodes.Single(n => n.Tag == "node");
        var sides = new[] { "south", "east", "north", "west" }.Select(tag => graph.Nodes.Single(n => n.Tag == tag)).ToArray();
        for (int side = 0; side < 4; side++)
        {
            var node = sides[side];
            if ((connections & (1 << side)) == 0) { graph.RemoveNode(node); continue; }
            int slot = (portals >> (side * 2)) & 3;
            if (side == 0) node.X = slot * 23 + 11;
            else if (side == 1) node.Y = slot * 23 + 11;
            else if (side == 2) node.X = (4 - slot) * 23 - 11;
            else node.Y = (4 - slot) * 23 - 11;
            var edge = node.Slots.First(e => e is not null)!;
            edge.Dungeon!.Accessible = true;
            node.Tag = ""; node.Descriptions.Add("accessible");
            var delta = side switch { 0 => (0, -1000), 1 => (1000, 0), 2 => (0, 1000), _ => (-1000, 0) };
            var outside = graph.AddNode((node.X + delta.Item1, node.Y + delta.Item2));
            outside.Placed = true; outside.Descriptions = [slot.ToString()];
            graph.AddEdge(node, outside, new GraphEdge { Type = edge.Type, Dungeon = new DungeonEdge { Accessible = true } });
        }
        var rooms = new RawDat(files.Raw("Data/DelveRooms.datc64") ?? throw new FileNotFoundException("DelveRooms"));
        int row = rooms.Find(r => rooms.Ref(r, 0) == 0 && rooms.Ref(r, 0x10) == feature);
        if (row < 0) throw new NotSupportedException($"No Mines room for Delve feature {feature}");
        var room = files.Room(rooms.String(row, 0x20)) ?? throw new FileNotFoundException("Delve encounter room");
        injected[record] = room.Path;
        centre.Tag = room.Tag;
        centre.Orientation = (byte)rng.Next(8);
        int degree = graph.Edges.Count(e => e.A == centre || e.B == centre);
        if (degree >= 3) throw new NotSupportedException("Branched Delve encounter cells");
        bool north = graph.Nodes.Contains(sides[2]), east = graph.Nodes.Contains(sides[1]);
        bool south = graph.Nodes.Contains(sides[0]), west = graph.Nodes.Contains(sides[3]);
        int footprint = Math.Max((room.Width - 1) / 6, (room.Height - 1) / 6);
        int maxX = east ? completed && footprint >= 2 ? 1 : 2 : 3;
        int maxY = north ? completed ? 1 : 2 : 3;
        centre.X = rng.Range(west ? 1 : 0, maxX) * 23;
        centre.Y = rng.Range(south ? 1 : 0, maxY) * 23;
        // 0x141f329e0 inserts a bend when two exits would share the centre's row or column.
        if (degree > 1)
        {
            int x = centre.X / 23, y = centre.Y / 23;
            Bend(sides[2], x + (x < 3 - ((portals >> 4) & 3) ? 1 : 0), 3);
            Bend(sides[0], x + (x < (portals & 3) ? 1 : 0), 0);
            Bend(sides[1], 3, y + (y < ((portals >> 2) & 3) ? 1 : 0));
            Bend(sides[3], 0, y + (y < 3 - (portals >> 6) ? 1 : 0));
        }
        void Bend(GraphNode side, int x, int y)
        {
            if (!graph.Nodes.Contains(side) || side.X / 23 == x && side.Y / 23 == y) return;
            var original = side.Slots.First(e => e is not null && (e.A == centre || e.B == centre))!;
            var copy = original.Copy();
            graph.RemoveEdge(original);
            var bend = graph.Nodes.FirstOrDefault(n => n.X == x * 23 && n.Y == y * 23) ?? graph.AddNode((x * 23, y * 23));
            graph.AddEdge(bend, side, original);
            graph.AddEdge(centre, bend, copy);
        }
    }
}
