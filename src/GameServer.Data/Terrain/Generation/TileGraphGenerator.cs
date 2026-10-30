namespace GameServer.Data.Terrain.Generation;

/// <summary>A tile graph (.tgr) subgraph: <c>0x14203f890</c> prepares the graph (tag choices, exist rolls, removals),
/// then <c>0x142116600</c> lays it out (Voronoi jitter first) on a view of the tile map: the primary subgraph on a map
/// of its own, a dungeon's subgraph at its record's corner on the shared map. Ported in client order, stage by stage;
/// the next stage that is not ported throws <see cref="NotSupportedException"/>.</summary>
sealed class TileGraphGenerator(TerrainCache files, LevelRandom rng, List<string> notes, bool vaalSideArea,
    Action<string, TileGraph>? onStage = null)
{
    public TileGraph Graph { get; private set; } = null!;

    /// <summary>The discoverable hideout rolled (generator +0xb).</summary>
    public bool Hideout { get; init; }

    /// <summary>The area is a town (WorldAreas IsTown): the walkability pass lets an area transition tile be enclosed.</summary>
    public bool Town { get; init; }

    /// <summary>The Labyrinth's graph pass, for a Labyrinth area.</summary>
    public LabyrinthPass? Labyrinth { get; init; }

    /// <summary>The area's TSI override (WorldAreas +0x192), the master in place of the graph's.</summary>
    public string? Master { get; init; }

    /// <summary>The generator's extra room pool (+0x40): the shrine count the features rolled.</summary>
    public ExtraRoomPool? Pool { get; init; }

    /// <summary>The pool the graph preparation adds the VaalSideArea row to when <see cref="Pool"/> is not set (a tile
    /// graph subgraph, whose later stages do not take the pool's rows).</summary>
    public ExtraRoomPool? PreparePool { get; init; }

    /// <summary>The graph kept a Vaal side area entrance (<c>0x142032320</c> returned true): the generator's +0xa is
    /// cleared for the graphs after it.</summary>
    public bool SideAreaKept { get; private set; }

    TileGrid? _grid;
    int _ox, _oy;

    /// <summary>The rooms placed in the subgraph (special nodes, overlays, fill rooms), in placing order.</summary>
    public List<PlacedRoom> Rooms { get; private set; } = [];

    /// <summary>The primary subgraph's record (the whole map) once <see cref="Generate"/> has run.</summary>
    public List<DoodadRecord> Records { get; private set; } = [];

    /// <summary>The subgraph's TileSet store (master +0x228), for the stages after the layout.</summary>
    public TileStore Store { get; private set; } = null!;

    /// <summary>The ground <c>0x1420289a0</c> floods the unwalkable border with (the string at 0x1435c4c50).</summary>
    internal const string ImpassableGround = "Metadata/Terrain/impassable_ground.gt";

    /// <summary>The finished grid. A stage that is not ported throws <see cref="PartialGenerationException"/> with the
    /// grid so far once the grid exists.</summary>
    public TileGrid Generate(string graphPath, int orientation)
    {
        try
        {
            return Layout(graphPath, orientation);
        }
        catch (NotSupportedException e) when (e is not PartialGenerationException && _grid is not null)
        {
            throw new PartialGenerationException(e.Message, _grid);
        }
    }

    /// <summary>The primary subgraph on a tile map of its own: the graph's size plus the outer buffer on every side,
    /// graph positions at the buffer; after the layout the post-unique tiles, the outer buffer and StoreyHeight.</summary>
    TileGrid Layout(string graphPath, int orientation)
    {
        var (masterPath, master, fills) = Stages(graphPath, orientation, null, 0, 0, primary: true);
        var grid = _grid!;
        int buffer = _ox;
        // 0x14203ece0: the TileSet's post-unique tiles.
        PostUniqueTiles.Run(rng, grid, buffer, buffer, Graph.Width, Graph.Height, Store, notes);
        // 0x142049d10 → 0x142044f30: per subgraph the outer ground (0x14201db80, needs the master's OuterGroundType) and
        // the outer buffer (0x14201ffa0).
        if (master.TryGetValue("OuterGroundType", out var outerGround))
        {
            // 0x14201db80 over the subgraph's view, the layout inside the buffer: a tile graph passes param_3 = 0, so
            // no height fixing.
            var ground = files.Ground(TerrainText.Resolve(masterPath, outerGround)) ?? throw new FileNotFoundException(outerGround);
            DungeonTiler.OuterGroundStart(grid, DungeonTiler.OuterTile(files, fills, ground),
                (buffer, buffer, buffer + Graph.Width, buffer + Graph.Height), fixHeights: false);
        }
        if (buffer != 0)
            new OuterBuffer(files, rng, new OuterBuffer.View(grid), buffer, buffer, buffer + Graph.Width, buffer + Graph.Height, Store, fills, notes).Run();
        // 0x142044f30 then runs the walkability pass (0x1420289a0) again for the primary record, over its whole
        // rectangle (the buffer included), and 0x142028870 flags the map's border vertices (the pass's border flags).
        // Only grounds and vertex flags change: the walkability grids read flag 8.
        new GroundStages(grid, 0, 0, grid.Width, grid.Height, files).Walkability(ImpassableGround, files.HasTransition, Town);
        // 0x14204cbd0 (after the layout 0x142049d10): every tile origin with a height offset has it multiplied by the
        // master's StoreyHeight (+0x14a, default 0), row by row (0x141d83060). Levels become the client's units here.
        int storey = int.Parse(master.GetValueOrDefault("StoreyHeight", "0"));
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
                if (grid[x, y] is { IsOrigin: true, HeightOffset: not 0 }) grid.ScaleHeight(x, y, storey);
        // The tile grid is final here (0x1420289a0 / 0x142028870 set grounds and entry flags only); the doodads
        // (0x1420425d0) run over the record afterwards (DoodadStage).
        Records = [new DoodadRecord(0, 0, grid.Width, grid.Height, master, masterPath, Rooms)];
        return grid;
    }

    /// <summary><c>0x14203f890</c> for a subgraph on a shared tile map (a dungeon's graph node, <c>0x14204f030</c>): the
    /// layout on the view at (<paramref name="x"/>, <paramref name="y"/>) of the graph's size. Map +0x10 (ground checks)
    /// goes on first. The walkability pass runs for the primary subgraphs and for a master that sets CountAsPrimary
    /// (taken to be +0x164). The post-unique tiles, outer ground and buffer and StoreyHeight are the caller's
    /// (<c>0x1420452e0</c>, <c>0x142044f30</c>, <c>0x14204cbd0</c>).</summary>
    internal (string MasterPath, Dictionary<string, string> Master, Dictionary<string, List<(int Weight, string Path, int Orientation)>> Fills) Subgraph(
        string graphPath, int orientation, TileGrid grid, int x, int y, bool primary)
    {
        grid.CheckGrounds = true;
        try
        {
            return Stages(graphPath, orientation, grid, x, y, primary);
        }
        catch (NotSupportedException e) when (e is not PartialGenerationException)
        {
            throw new PartialGenerationException(e.Message, grid);
        }
    }

    /// <summary><c>0x14203f890</c> → <c>0x142116600</c> on the view at (<paramref name="x"/>, <paramref name="y"/>) of
    /// <paramref name="shared"/>, or on a grid of its own (the view at the outer buffer) when null.</summary>
    (string MasterPath, Dictionary<string, string> Master, Dictionary<string, List<(int Weight, string Path, int Orientation)>> Fills) Stages(
        string graphPath, int orientation, TileGrid? shared, int x, int y, bool primary)
    {
        Graph = TileGraph.Parse(files.Text(graphPath) ?? throw new FileNotFoundException(graphPath));
        if (orientation != 0) throw new NotSupportedException($"turning a tile graph (orientation {orientation}) is not ported");
        SideAreaKept = Prepare(Graph, rng, vaalSideArea, Hideout, primary, Pool ?? PreparePool, Labyrinth);
        notes.Add($"graph {graphPath}: {Graph.Nodes.Count} nodes, {Graph.Edges.Count} edges after exist rolls");
        var cells = new VoronoiCells(Graph);
        cells.Jitter(rng);

        string masterPath = Master ?? TerrainText.Resolve(graphPath, Graph.MasterFile);
        var master = TerrainText.KeyValues(files.Text(masterPath) ?? throw new FileNotFoundException(masterPath));
        TileGrid grid;
        if (shared is null)
        {
            // The tile map has the graph's size plus the outer buffer on every side; graph positions sit at the buffer.
            if (master.GetValueOrDefault("OuterBufferSize", "0").Split(' ') is { Length: > 1 })
                throw new NotSupportedException($"{masterPath}: an OuterBufferSize with several margins on a tile graph's own map is not ported");
            int buffer = int.Parse(master.GetValueOrDefault("OuterBufferSize", "0"));
            grid = new TileGrid(Graph.Width + 2 * buffer, Graph.Height + 2 * buffer) { GroundFlags = g => files.Ground(g)?.Flags ?? 0, Unmirrored = files.Unmirrored };
            (_ox, _oy) = (buffer, buffer);
        }
        else
        {
            grid = shared;
            (_ox, _oy) = (x, y);
            if (x < 0 || y < 0 || x + Graph.Width > grid.Width || y + Graph.Height > grid.Height)
                throw new InvalidOperationException($"Subgraph {graphPath} extends outside the TileMap!");
        }
        _grid = grid;
        var store = Store = new TileStore(files, files.TileList(TerrainText.Resolve(masterPath, master["TileSet"])));
        var rooms = new RoomSetIndex(files, TerrainText.Resolve(masterPath, master["RoomSet"]));
        var special = new SpecialNodes(files, rng, notes, Graph, grid, _ox, _oy, store, rooms) { BeforeRelax = () => onStage?.Invoke("relax", Graph) };
        special.Run();
        Rooms = special.Rooms;
        onStage?.Invoke("special", Graph);
        // Edges whose type is a wildcard leave the graph (0x141f4f130).
        foreach (var e in Graph.Edges.Where(e => files.Edge(e.Type)?.Wildcard == true).ToList()) Graph.RemoveEdge(e);
        var router = new EdgeRouter(Graph, grid, _ox, _oy, rng, e => files.Edge(e.Type) ?? throw new FileNotFoundException(e.Type));
        // 0x142122d90: the special stage's failed nodes, then plain nodes with more than two edges, are exploded.
        foreach (var n in special.Failed) special.Explode(n);
        foreach (var n in special.Plain.Where(n => n.Degree > 2)) special.Explode(n);
        onStage?.Invoke("beforeRoute", Graph);
        router.Run();
        onStage?.Invoke("route", Graph);
        new EdgeShaping(Graph, e => files.Edge(e.Type) ?? throw new FileNotFoundException(e.Type)).Run();
        onStage?.Invoke("corners", Graph);
        foreach (var n in special.Failed)
        {
            special.TileLessVirtual(n);
            onStage?.Invoke("virtualNode", Graph);
        }
        new EdgeSplitting(Graph, rng, e => files.Edge(e.Type) ?? throw new FileNotFoundException(e.Type), special.Plain,
            t => files.Edge(t) ?? throw new FileNotFoundException(t)).Run();
        onStage?.Invoke("split", Graph);
        new LinkMerge(Graph, special.Plain).Run();
        onStage?.Invoke("merge", Graph);
        // 0x142003bc0, then another corner rounding (0x14210aeb0, up to five rounds), then the plain nodes.
        Graph.Nodes = [.. Graph.Nodes.OrderByDescending(n => n.Priority)];
        var shaping = new EdgeShaping(Graph, e => files.Edge(e.Type) ?? throw new FileNotFoundException(e.Type));
        shaping.RoundAll();
        special.RunPlain();
        onStage?.Invoke("plain", Graph);
        // 0x142114ea0: every node's edge slots move to the front, in order; then another corner rounding.
        foreach (var n in Graph.Nodes)
        {
            var edges = n.Slots.Where(e => e is not null).ToArray();
            for (int i = 0; i < 4; i++) n.Slots[i] = i < edges.Length ? edges[i] : null;
        }
        shaping.RoundAll();
        onStage?.Invoke("beforeTiling", Graph);
        // The edge types whose tiles get their open sides filled (0x141feed20) are listed in the FillTiles object
        // (master +0x218, +0x140).
        string? gftPath = master.TryGetValue("FillTiles", out var gft) ? TerrainText.Resolve(masterPath, gft) : null;
        var edgePoints = gftPath is null ? [] : files.FillEdgeTypes(gftPath);
        var tiling = new EdgeTiling(Graph, grid, _ox, _oy, rng, e => files.Edge(e.Type) ?? throw new FileNotFoundException(e.Type), files, store, notes, edgePoints);
        tiling.Run();
        special.RunLonely();
        // The vertex grounds (0x142110a20, 0x142118470 heights, 0x142110d60, 0x141feed20).
        var grounds = new GroundStages(grid, _ox, _oy, Graph.Width, Graph.Height, files);
        grounds.Lock();
        grounds.Flood();
        var heights = new HeightLevels(grid, _ox, _oy, Graph.Width, Graph.Height, files);
        heights.Run();
        // 0x1420289a0: the primary subgraphs (generator +0x30 +9) and those whose master has +0x164.
        if (primary || master.ContainsKey("CountAsPrimary")) grounds.Walkability(ImpassableGround, files.HasTransition, Town);
        // 0x142110d60's levels: the master's +0x146 / +0x144 when set (not ported: no ported master sets them), else
        // the view's highest and lowest vertex heights, 0 without heights.
        var levels = grid.HasHeights ? heights.Range() : (0, 0);
        string? GraphGround(string g) => g.Length == 0 ? null : TerrainText.Resolve(graphPath, g);
        // The graph's top ground (+0xa0) goes on the highest level, its bottom ground (+0xa8) on the lowest, its
        // default ground (+0xb0) on every vertex still without one.
        LevelGenerator.PaintStarting?.Invoke(grid, _ox, _oy, Graph.Width, Graph.Height);
        grounds.FillUnset(GraphGround(Graph.DefaultGround), GraphGround(Graph.TopGround), GraphGround(Graph.BottomGround), levels);
        if (tiling.EdgePointTiles.Count != 0)
        {
            var sideFill = new Fill(files, rng, grid, _ox, _oy, Graph.Width, Graph.Height, store, files.FillTiles(gftPath!), special.Rooms, notes);
            grounds.EdgePoints(tiling.EdgePointTiles, edgePoints, sideFill.FillSide);
        }
        grounds.Unlock();
        // The pool's area unique rooms (0x142115390, master +0x92 taken to be off), the shrines (0x142115cc0) and the
        // pre-unique rooms (0x1421162d0), each tilised as a fill room (0x141fe89e0). The fill object's listener
        // (0x141fef240) is still idle: the levels are not stored yet (2_6_2 seed 1814151108's shrine keeps trims1).
        var roomSet = new DungeonRoomSet(files, TerrainText.Resolve(masterPath, master["RoomSet"]));
        var placer = new Fill(files, rng, grid, _ox, _oy, Graph.Width, Graph.Height, store,
            gftPath is null ? new Dictionary<string, List<(int Weight, string Path, int Orientation)>>() : files.FillTiles(gftPath), special.Rooms, notes);
        AreaUniqueRooms(placer);
        Shrines(roomSet, placer);
        PreUniqueRooms(roomSet, placer);
        // +0x90 / +0x94 = the levels: from here on the listener repaints the level grounds under every tile that room
        // tilising and the fill place.
        var painter = grounds.LevelPainter(GraphGround(Graph.TopGround), GraphGround(Graph.BottomGround), levels);
        special.TilePlaced = painter;
        // 0x141ffe240, then the first overlay pass (0x141fff1a0 with bit 1).
        var passes = master.TryGetValue("Overlays", out var toy) ? Overlays.Parse(files, TerrainText.Resolve(masterPath, toy)) : [];
        var overlays = new Overlays(files, rng, notes, grid, _ox, _oy, Graph.Width, Graph.Height, special.Tiler, [.. special.Rooms],
            int.Parse(master.GetValueOrDefault("RoomOverlap", "1")), Pool?.Placed);
        foreach (var pass in passes.Where(p => (p.Passes & 1) != 0)) overlays.Run(pass, saveTiles: false);
        onStage?.Invoke("overlays", Graph);
        // 0x141fef070, then the second overlay pass (bit 2, after the fill).
        if (gftPath is null) throw new InvalidOperationException("Cannot tilise blank: Fill tile list is not specified");
        LevelGenerator.FillStarting?.Invoke(grid, _ox, _oy, Graph.Width, Graph.Height);
        var fills = files.FillTiles(gftPath);
        var fill = new Fill(files, rng, grid, _ox, _oy, Graph.Width, Graph.Height, store, fills, special.Rooms, notes) { TilePlaced = painter };
        fill.Run();
        overlays.FillEntry = (x, y, g, open) => fill.FillEntry(x, y, g, open);
        foreach (var pass in passes.Where(p => (p.Passes & 2) != 0)) overlays.Run(pass, saveTiles: true);
        // 0x1421193d0 (storeys: plain tiles take their neighbours' heights), then the mirror seams (0x142025440).
        new Storeys(grid, _ox, _oy, Graph.Width, Graph.Height, files).Run();
        new Seams(files, rng, grid, _ox, _oy, Graph.Width, Graph.Height, store, special.Rooms, special.RoomVertices,
            (GraphGround(Graph.TopGround), GraphGround(Graph.BottomGround), GraphGround(Graph.DefaultGround)), notes).Run();
        return (masterPath, master, fills);
    }

    /// <summary><c>0x142115140</c>: the view's entries, row by row, that are empty, whose vertex has no ground or one
    /// with .gt flags 4 and 8, and that is not unwalkable (vertex flag 8); <c>Next(count)</c> (its value unused), then
    /// shuffled (<c>0x14212d7f0</c>).</summary>
    List<(int X, int Y)> Openings()
    {
        var grid = _grid!;
        var list = new List<(int X, int Y)>();
        for (int y = 0; y < Graph.Height; y++)
            for (int x = 0; x < Graph.Width; x++)
            {
                if (grid[x + _ox, y + _oy] is not null) continue;
                ref var c = ref grid.Corner(x + _ox, y + _oy);
                int flags = c.Ground is null ? 0 : files.Ground(c.Ground)?.Flags ?? 0;
                if (c.Ground is not null && (flags & 0xc) != 0xc) continue;
                if ((c.Flags & 8) != 0) continue;
                list.Add((x, y));
            }
        rng.Next((uint)list.Count);
        rng.Shuffle(list);
        return list;
    }

    /// <summary>The turned w × h lies inside the view on empty entries (<c>0x141d84e10</c>).</summary>
    bool Fits(int x, int y, int w, int h)
    {
        if (x >= Graph.Width || x + w > Graph.Width || y >= Graph.Height || y + h > Graph.Height) return false;
        for (int v = 0; v < h; v++)
            for (int u = 0; u < w; u++)
                if (!_grid!.Inside(x + u + _ox, y + v + _oy) || _grid[x + u + _ox, y + v + _oy] is not null) return false;
        return true;
    }

    /// <summary><c>0x142115390</c>: with rows in the pool, the openings once; the rows in order, while the share (+0x144)
    /// lasts, each place a room (<c>0x142115680</c>): its only room, else its rooms shuffled (<c>Next(count)</c>, then
    /// the shuffle) until one is placed. The rows placed leave the pool, counted (<c>0x141d8b2c0</c>).</summary>
    void AreaUniqueRooms(Fill placer)
    {
        if (Pool is not { } pool || pool.Rows.Count == 0) return;
        // 0x14203f890: the primary tile graph takes its share (all the rows: no subgraph weight to share by).
        pool.Spread(0, rng);
        var openings = Openings();
        var placed = new List<ExtraFeatureRow>();
        int left = pool.Share;
        foreach (var row in pool.Rows.ToList())
        {
            if (left == 0) break;
            bool any = row.Orientations.Count == 0 || row.Orientations.Contains(8);
            bool done;
            if (row.Rooms.Count == 1) done = PlaceUnique(placer, row, row.Rooms[0], any, openings);
            else
            {
                var rooms = row.Rooms.ToList();
                rng.Next((uint)rooms.Count);
                rng.Shuffle(rooms);
                done = false;
                foreach (var r in rooms)
                    if (PlaceUnique(placer, row, r, any, openings)) { done = true; break; }
            }
            if (done) { placed.Add(row); left--; }
        }
        foreach (var row in placed) pool.Remove(row, true);
    }

    /// <summary><c>0x142115680</c>: a room whose own key has no edges and one ground on its corners turns by
    /// <c>Next(8)</c> (the row allows any) or one of the row's orientations (<c>Next(count)</c>), and goes on the first
    /// opening whose vertex ground is none or flags 4 and 8 where it fits; the vertices it covers that have no ground
    /// or a flag-8 one take the opening's ground, when that has none or flag 8. Tilised at the vertex height (open);
    /// the caller's copy of the share drops by one.</summary>
    bool PlaceUnique(Fill placer, ExtraFeatureRow row, string path, bool any, List<(int X, int Y)> openings)
    {
        var room = files.Room(path) ?? throw new FileNotFoundException(path);
        if (room.Key.Edges.Any(e => e is not null) || room.Key.Grounds.Distinct().Count() != 1) return false;
        int o = any ? (int)rng.Next(8u) : row.Orientations[(int)rng.Next((uint)row.Orientations.Count)];
        var (w, h) = Orientation.Size(o, room.Width, room.Height);
        var grid = _grid!;
        bool Loose(string? g) => g is null || ((files.Ground(g)?.Flags ?? 0) & 8) != 0;
        foreach (var (x, y) in openings)
        {
            string? ground = grid.Corner(x + _ox, y + _oy).Ground;
            if (ground is not null && ((files.Ground(ground)?.Flags ?? 0) & 0xc) != 0xc) continue;
            if (!Fits(x, y, w, h)) continue;
            for (int v = 0; v <= h; v++)
                for (int u = 0; u <= w; u++)
                    if (Loose(grid.Corner(x + u + _ox, y + v + _oy).Ground) && Loose(ground))
                        grid.SetGround(x + u + _ox, y + v + _oy, ground);
            notes.Add($"area unique room {Path.GetFileName(room.Path)} ({row.Name}) o{o} at ({x + _ox}, {y + _oy})");
            placer.PlaceRoom(room, x, y, o, grid.Corner(x + _ox, y + _oy).Height, ground, true);
            Pool!.RoomPlaced(x + _ox, y + _oy, w, h);
            return true;
        }
        return false;
    }

    /// <summary><c>0x142115cc0</c>: with shrine rooms in the RoomSet (+0x118, tag "shrine") and shrines rolled (pool
    /// +0x148), the openings in order while shrines are left: one whose vertex ground is none or flags 4 and 8, not
    /// within 5 entries of a shrine placed here, takes a shrine room that fits it (0x55 as it is, 0xaa turned):
    /// <c>Next(count)</c> among them, the orientation <c>Next(840)</c> from its mask, tilised at the vertex height with
    /// the vertex ground (open); a shrine placed (<c>0x141d8b9a0</c>).</summary>
    void Shrines(DungeonRoomSet roomSet, Fill placer)
    {
        var rooms = roomSet.All.Where(r => r.Tag == "shrine" && r.Def is not null).Select(r => r.Def!).ToList();
        if (rooms.Count == 0 || Pool is not { Shrines: > 0 } pool) return;
        var placed = new List<(int X, int Y)>();
        foreach (var (x, y) in Openings())
        {
            if (pool.Shrines == 0) break;
            ref var c = ref _grid!.Corner(x + _ox, y + _oy);
            string? ground = c.Ground;
            if (ground is not null && ((files.Ground(ground)?.Flags ?? 0) & 0xc) != 0xc) continue;
            if (placed.Any(p => (uint)((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y)) < 25)) continue;
            var fitting = new List<(RoomDef Room, byte Mask)>();
            foreach (var room in rooms)
            {
                byte mask = (byte)((Fits(x, y, room.Width, room.Height) ? 0x55 : 0) | (Fits(x, y, room.Height, room.Width) ? 0xaa : 0));
                if (mask != 0) fitting.Add((room, mask));
            }
            if (fitting.Count == 0) continue;
            placed.Add((x, y));
            var pick = fitting[(int)rng.Next((uint)fitting.Count)];
            int o = LevelRandom.OrientationFromMask(pick.Mask, rng.Next(840u));
            notes.Add($"shrine {Path.GetFileName(pick.Room.Path)} o{o} at ({x + _ox}, {y + _oy})");
            placer.PlaceRoom(pick.Room, x, y, o, c.Height, ground, true);
            var (sw, sh) = Orientation.Size(o, pick.Room.Width, pick.Room.Height);
            pool.ShrinePlaced(x + _ox, y + _oy, sw, sh);
        }
    }

    /// <summary><c>0x142116600</c> → <c>0x1421162d0</c>: with "preunique" rooms in the RoomSet (+0x148, with their line's
    /// orientation mask), each in list order takes the first opening where it fits, an orientation drawn for every
    /// opening tried (<c>Next(840)</c> from the mask); tilised at the vertex height with the vertex ground, open when
    /// that has none or flag 4. One that fits nowhere is fatal.</summary>
    void PreUniqueRooms(DungeonRoomSet roomSet, Fill placer)
    {
        var rooms = roomSet.All.Where(r => r.Tag == "preunique" && r.Def is not null).ToList();
        if (rooms.Count == 0) return;
        var openings = Openings();
        foreach (var room in rooms)
        {
            bool done = false;
            foreach (var (x, y) in openings)
            {
                int o = LevelRandom.OrientationFromMask((byte)room.Mask, rng.Next(840u));
                var (w, h) = Orientation.Size(o, room.Def!.Width, room.Def.Height);
                if (!Fits(x, y, w, h)) continue;
                ref var c = ref _grid!.Corner(x + _ox, y + _oy);
                string? ground = c.Ground;
                bool open = ground is null || ((files.Ground(ground)?.Flags ?? 0) & 4) != 0;
                notes.Add($"pre-unique room {Path.GetFileName(room.Path)} o{o} at ({x + _ox}, {y + _oy})");
                placer.PlaceRoom(room.Def, x, y, o, c.Height, ground, open);
                done = true;
                break;
            }
            if (!done) throw new InvalidOperationException($"Failed to place pre-unique room {room.Path}");
        }
    }

    /// <summary><c>0x14203f890</c> up to the layout (the dungeon graphs' <c>0x14203c900</c> makes the same draws), for
    /// the subgraph record <paramref name="primary"/> says is the first (the passes that take the record index). True
    /// when a Vaal side area entrance was kept (the generator's +0xa is then cleared).</summary>
    internal static bool Prepare(TileGraph graph, LevelRandom rng, bool vaalSideArea, bool hideout, bool primary, ExtraRoomPool? pool,
        LabyrinthPass? labyrinth = null)
    {
        foreach (var n in graph.Nodes)
        {
            // 0x14203a600: a random offset inside the node's rectangle (x1..x2, y1..y2), each axis drawn inclusively
            // (0x141ff97f0) when its range is not empty (MapWorldsTheatreOfLies, 2026-10-27).
            if (n.OffsetX1 != n.OffsetX2 || n.OffsetY1 != n.OffsetY2 || n.OffsetX1 != 0 || n.OffsetY1 != 0)
            {
                int dx = n.OffsetX1 < n.OffsetX2 ? n.OffsetX1 + rng.Range(0, n.OffsetX2 - n.OffsetX1) : n.OffsetX1;
                int dy = n.OffsetY1 < n.OffsetY2 ? n.OffsetY1 + rng.Range(0, n.OffsetY2 - n.OffsetY1) : n.OffsetY1;
                n.X += dx;
                n.Y += dy;
            }
            // 0x14203a180 → 0x142036490: a node whose "league=" names another league than the instance's (world
            // +0xd0) is dropped; the port has no instance league. "shuffle…" and "unique…" are read after the exist
            // rolls (ShuffleGroups, UniqueGroups), "subarea=…" when the record is added (LevelGenerator.SubAreas).
            if (n.Descriptions.Any(d => d.StartsWith("league=", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException($"{n}: league= nodes (0x142036490, the instance's league) are not ported");
            if (n.Tag == "league") n.Exist = 0;
        }
        // 0x14203a480: a tag listing alternatives "a,b,c" keeps one of them.
        foreach (var n in graph.Nodes)
        {
            if (!n.Tag.Contains(',')) continue;
            var parts = n.Tag.Split(',');
            if (parts.Length > 1) n.Tag = parts[(int)rng.Next((uint)parts.Length)];
        }
        bool? sideArea = SideAreas(graph, rng, vaalSideArea);
        // Without side area nodes the rolled Vaal side area becomes the VaalSideArea extra terrain feature (its room
        // `vaal_side_area.arm`) and +0xa stays set, so every later graph without nodes adds it again.
        if (sideArea is null && vaalSideArea) (pool ?? throw new InvalidOperationException("no extra room pool")).Add("VaalSideArea", 1);
        // 0x142032760 / 0x142033e30: the Labyrinth's pass (secret nodes, the gold door, boss overrides).
        labyrinth?.Run(graph, rng);
        // 0x142035350 / 0x142034e60, for the first record only: a node with "subarea=hideout…" or
        // "subareatype=Hideout…" is dropped (exist 0, no descriptions) when no discoverable hideout rolled; with one,
        // that description names the hideout's claim subarea instead (Hideouts.dat; no draw or tile reads it, left as
        // it is).
        if (primary && !hideout)
            foreach (var n in graph.Nodes)
                if (n.Descriptions.Any(d => d.StartsWith("subarea=hideout", StringComparison.Ordinal) || d.StartsWith("subareatype=Hideout", StringComparison.Ordinal)))
                {
                    n.Exist = 0;
                    n.Descriptions.Clear();
                }
        // 0x141f29730, the Betrayal plugin (generator plugin vtable +0x40) for a record after the first: the
        // "targetroom2" nodes take a Next(count) draw and a shuffle, "targetroom1" goes in front, and each takes the
        // server's next Betrayal target (BetrayalTargets.dat +0x59, its room) or exist 0 when none is left. The server
        // sends no targets, so all of them drop (the Betrayal safe houses).
        if (!primary && graph.Nodes.Any(n => n.Tag is "targetroom1" or "targetroom2"))
        {
            var targets = graph.Nodes.Where(n => n.Tag == "targetroom2").ToList();
            rng.Next((uint)targets.Count);
            rng.Shuffle(targets);
            foreach (var n in graph.Nodes.Where(n => n.Tag == "targetroom1").Take(1).Concat(targets)) n.Exist = 0;
        }
        // 0x142031390: exist rolls, nodes then edges; failed edges go first, then failed nodes.
        var nodesOut = new List<GraphNode>();
        foreach (var n in graph.Nodes)
        {
            if (n.Exist != 0)
            {
                if (n.Exist > 99) continue;
                if (rng.Next(100u) < (uint)n.Exist) continue;
            }
            nodesOut.Add(n);
        }
        var edgesOut = new List<GraphEdge>();
        foreach (var e in graph.Edges)
            if (e.Exist == 0 || rng.Next(100u) >= (uint)e.Exist) edgesOut.Add(e);
        foreach (var e in edgesOut) graph.RemoveEdge(e);
        foreach (var n in nodesOut) graph.RemoveNode(n);
        ShuffleGroups(graph, rng);
        UniqueGroups(graph, rng);
        // 0x142003bc0: stable sort by priority, highest first.
        graph.Nodes = [.. graph.Nodes.OrderByDescending(n => n.Priority)];
        return sideArea == true;
    }

    /// <summary><c>0x142030790</c> after the exist rolls, with <c>0x14203a720</c>: a node's first description starting
    /// "shuffle" puts it in group N (the number after it, none = 0; not a number or below 0: no group). Per group in
    /// order of N, while it holds two or more: <c>Next(size)</c> twice, both nodes leave the group, and two different
    /// nodes swap tags (+0x20) and descriptions (+8).</summary>
    static void ShuffleGroups(TileGraph graph, LevelRandom rng)
    {
        var groups = new List<List<GraphNode>>();
        foreach (var n in graph.Nodes)
        {
            var d = n.Descriptions.FirstOrDefault(d => d.StartsWith("shuffle", StringComparison.Ordinal));
            if (d is null) continue;
            int g = d.Length == 7 ? 0 : LeadingInt(d[7..]) ?? -1;
            if (g < 0) continue;
            while (groups.Count <= g) groups.Add([]);
            groups[g].Add(n);
        }
        foreach (var group in groups)
            while (group.Count > 1)
            {
                var a = group[(int)rng.Next((uint)group.Count)];
                var b = group[(int)rng.Next((uint)group.Count)];
                group.Remove(a);
                group.Remove(b);
                if (a == b) continue;
                (a.Tag, b.Tag) = (b.Tag, a.Tag);
                (a.Descriptions, b.Descriptions) = (b.Descriptions, a.Descriptions);
            }
    }

    /// <summary><c>0x142030790</c> after the shuffle groups, with <c>0x142031100</c> / <c>0x142035c70</c>: a node's first
    /// description starting "unique" reads "unique[letter][N][#count]" (none = group 0, count 1; N not a number: no
    /// group; a count that is not a number: 1). Per group in order of N, keeping the largest count: with more nodes than
    /// the count, <c>Next(size)</c> takes one to keep, count times, and the nodes left are removed
    /// (<c>0x142035f10</c>, as a failed node). Then the lettered groups ("uniqueA1#2", the letter upper-cased), in order of
    /// first appearance: the first record with the letter draws which of its numbered groups stay (their indices,
    /// <c>Next(count)</c>, a partial shuffle of count steps, <c>0x142037ea0</c>, then cut to count); a group not among them
    /// loses its nodes, in every record of the level (1_3_17_2's recipe entrances, 2026-10-27).</summary>
    static void UniqueGroups(TileGraph graph, LevelRandom rng)
    {
        var groups = new List<(int Count, List<GraphNode> Nodes)>();
        var lettered = new List<(char Letter, int Count, List<List<GraphNode>> Groups)>();
        foreach (var n in graph.Nodes)
        {
            var d = n.Descriptions.FirstOrDefault(d => d.StartsWith("unique", StringComparison.Ordinal));
            if (d is null) continue;
            int count = 1, g = 0;
            if (d.Length > 6)
            {
                string rest = d[6..];
                char letter = char.ToUpperInvariant(rest[0]);
                if (letter is >= 'A' and <= 'Z') rest = rest[1..];
                else letter = '\0';
                int hash = rest.IndexOf('#');
                if (hash >= 0)
                {
                    if (hash + 1 < rest.Length) count = LeadingInt(rest[(hash + 1)..]) ?? 1;
                    rest = rest[..hash];
                }
                if (LeadingInt(rest) is not { } v || v < 0) continue;
                g = v;
                if (letter != '\0')
                {
                    int li = lettered.FindIndex(l => l.Letter == letter);
                    if (li < 0) { lettered.Add((letter, 0, [])); li = lettered.Count - 1; }
                    var (lt, lc, lg) = lettered[li];
                    while (lg.Count <= g) lg.Add([]);
                    lg[g].Add(n);
                    lettered[li] = (lt, Math.Max(lc, count), lg);
                    continue;
                }
            }
            while (groups.Count <= g) groups.Add((0, []));
            groups[g] = (Math.Max(groups[g].Count, count), groups[g].Nodes);
            groups[g].Nodes.Add(n);
        }
        foreach (var (count, nodes) in groups)
        {
            if (count >= nodes.Count) continue;
            for (int k = 0; k < count; k++) nodes.RemoveAt((int)rng.Next((uint)nodes.Count));
            foreach (var n in nodes) graph.RemoveNode(n);
        }
        foreach (var (letter, count, letterGroups) in lettered)
        {
            if (!rng.UniqueLetters.TryGetValue(letter, out var keep))
            {
                rng.UniqueLetters[letter] = keep = [];
                if (count != 0)
                {
                    keep.AddRange(Enumerable.Range(0, letterGroups.Count));
                    rng.Next((uint)keep.Count);
                    rng.Shuffle(keep, count);
                    if (count < keep.Count) keep.RemoveRange(count, keep.Count - count);
                }
            }
            for (int i = 0; i < letterGroups.Count; i++)
                if (!keep.Contains(i))
                    foreach (var n in letterGroups[i]) graph.RemoveNode(n);
        }
    }

    /// <summary>A number as <c>std::wistream &gt;&gt; int</c> reads it: leading blanks, a sign, digits; null when none.</summary>
    static int? LeadingInt(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        int start = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        int digits = i;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        return i > digits && int.TryParse(s[start..i], out int v) ? v : null;
    }

    /// <summary><c>0x142032320</c>: nodes with a description "sidearea&lt;N&gt;" (the first such, N from '1'; none = 1) are
    /// Vaal side area entrances, grouped by N. When the area rolled a Vaal side area (generator +0xa), one node of a
    /// random group keeps its entrance: <c>Next(groups)</c>, <c>Next(group size)</c> (unused), the group shuffled, its
    /// first node loses its "unique…" descriptions, gets exist 100 and "accessible". Every other grouped node gets exist
    /// 0 with its descriptions cleared, so the exist rolls remove it (a tagged one stays as a plain node). The
    /// dungeon graphs' copy (<c>0x142031d10</c>, from <c>0x14203c900</c>) groups through the same <c>0x14203a410</c>.
    /// True when an entrance was kept, null when the graph has no side area nodes.</summary>
    static bool? SideAreas(TileGraph graph, LevelRandom rng, bool vaalSideArea)
    {
        var groups = new List<List<GraphNode>>();
        foreach (var n in graph.Nodes)
        {
            var d = n.Descriptions.FirstOrDefault(d => d.Length > 7 && d.StartsWith("sidearea", StringComparison.Ordinal));
            if (d is null) continue;
            int g = d.Length < 9 ? 0 : d[8] - '1';
            if (g < 0) throw new NotSupportedException($"{n}: side area group '{d}'");
            while (groups.Count <= g) groups.Add([]);
            groups[g].Add(n);
        }
        groups.RemoveAll(g => g.Count == 0);
        if (groups.Count == 0) return null;
        if (vaalSideArea)
        {
            var group = groups[(int)rng.Next((uint)groups.Count)];
            rng.Next((uint)group.Count);
            rng.Shuffle(group);
            var keep = group[0];
            keep.Descriptions.RemoveAll(d => d.StartsWith("unique", StringComparison.Ordinal));
            keep.Exist = 100;
            keep.Descriptions.Add("accessible");
            group.RemoveAt(0);
        }
        foreach (var n in groups.SelectMany(g => g))
        {
            n.Exist = 0;
            n.Descriptions.Clear();
        }
        return vaalSideArea;
    }
}

/// <summary>Generation stopped at a part that is not ported; <see cref="Grid"/> holds the tiles placed so far.</summary>
public sealed class PartialGenerationException(string message, TileGrid grid) : NotSupportedException(message)
{
    public TileGrid Grid { get; } = grid;
}
