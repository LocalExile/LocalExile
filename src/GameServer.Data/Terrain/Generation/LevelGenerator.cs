namespace GameServer.Data.Terrain.Generation;

/// <summary>A topology an area can use (Topologies.dat): the graph file, its allowed orientations (+0x1c) and its
/// variant (+0x2c; 60 = default).</summary>
public sealed record TopologyInfo(string Graph, IReadOnlyList<int> Orientations, int Variant = 60);

/// <summary>What the generator needs to know about the area, read at the client's offsets of its WorldAreas row
/// (Agents/docs/re/011-level-generation.md §1): +0x14 town, +0x2a HASH16, +0x68 topologies, +0xe4 map area, +0x14e
/// randomness off (hideouts), +0x1e4 area type, +0x22a league chances (a WorldAreaLeagueChances row, read by byte
/// offset), and the Hideouts row discoverable in it (+0x42 enabled, +0x43 chance in 10 000). <see cref="League"/> is a
/// CoreLeagues row by index: +0x8a most per area, +0x8e needs a chance, +0xa0 not rolled here. <see cref="HideoutClaim"/>
/// is that Hideouts row's claim subarea (+0x2a, a WorldAreas id), <see cref="SubArea"/> the topologies of a WorldAreas
/// row by id (a graph node's "subarea=…"), <see cref="SubAreaRow"/> by row index (an extra terrain feature's world
/// area). +0x171 marks a Labyrinth area, +0x1bb a Labyrinth boss area. +0x192 is the
/// area's TSI override (<see cref="Master"/>: the EndGame Labyrinth's master_endgame.tsi, the Delve biomes').</summary>
public sealed record AreaInfo(string Id, bool IsTown, IReadOnlyList<TopologyInfo> Topologies, bool RandomnessDisabled = false,
    ushort Hash16 = 0, bool IsMapArea = false, int AreaType = 0, int Level = 1, Func<int, int>? LeagueChance = null,
    (bool Enabled, int Chance)? DiscoverableHideout = null, Func<int, (int Max, bool NeedsChance, bool Skip)>? League = null,
    string? HideoutClaim = null, Func<string, IReadOnlyList<TopologyInfo>?>? SubArea = null, bool IsLabyrinth = false,
    bool IsLabyrinthBoss = false, string? Master = null, Func<int, IReadOnlyList<TopologyInfo>?>? SubAreaRow = null)
{
    /// <summary>The area's row and the rows it points to, from the game's tables.</summary>
    public static AreaInfo Load(Func<string, byte[]?> read, string areaId, int level)
    {
        RawDat Dat(string name) => new(read($"Data/{name}.datc64") ?? throw new FileNotFoundException($"Data/{name}.datc64"));
        var areas = Dat("WorldAreas");
        var topologies = Dat("Topologies");
        int row = areas.Find(r => areas.String(r, 0) == areaId);
        if (row < 0) throw new ArgumentException($"no WorldAreas row {areaId}");
        List<TopologyInfo> Tops(int r) => areas.RefList(r, 0x68)
            .Select(t => new TopologyInfo(topologies.String(t, 8), topologies.I32List(t, 0x1c), topologies.I32(t, 0x2c))).ToList();
        var tops = Tops(row);
        int chancesRow = areas.Ref(row, 0x22a);
        Func<int, int>? chance = null;
        if (chancesRow >= 0)
        {
            var chances = Dat("WorldAreaLeagueChances");
            chance = at => chances.I32(chancesRow, at);
        }
        var leagues = Dat("CoreLeagues");
        var hideouts = Dat("Hideouts");
        int hideout = hideouts.Find(h => hideouts.RefList(h, 0x1a).Contains(row));
        int claim = hideout < 0 ? -1 : hideouts.Ref(hideout, 0x2a);
        return new AreaInfo(areaId, areas.Bool(row, 0x14), tops, areas.Bool(row, 0x14e), areas.U16(row, 0x2a), areas.Bool(row, 0xe4),
            areas.I32(row, 0x1e4), level, chance, hideout < 0 ? null : (hideouts.Bool(hideout, 0x42), hideouts.I32(hideout, 0x43)),
            i => (leagues.I32(i, 0x8a), leagues.Bool(i, 0x8e), leagues.Bool(i, 0xa0)),
            claim < 0 ? null : areas.String(claim, 0),
            id => areas.Find(r => areas.String(r, 0) == id) is var r and >= 0 ? Tops(r) : null, areas.Bool(row, 0x171), areas.Bool(row, 0x1bb),
            areas.String(row, 0x192) is { Length: > 0 } tsi ? tsi : null, r => r < areas.Count ? Tops(r) : null);
    }
}

/// <summary>The generated tile grid and the subgraph records (rectangle, master, placed rooms) the doodad stage
/// (<see cref="DoodadStage"/>) runs over.</summary>
public sealed record GenerationResult(TileGrid Grid, LevelRandom Random, TopologyInfo Topology, IReadOnlyList<string> Notes,
    IReadOnlyList<DoodadRecord> Records);

/// <summary>The server's port of the client's level generator (<c>0x14204d820</c>, Agents/docs/re/011-level-generation.md).
/// Ported so far: .dgr layouts (one node: towns; several: dungeons, Agents/docs/re/011-level-generation.md §3c2) and
/// .tgr tile graphs, up to the doodads. Every step names the client function it follows.</summary>
public static class LevelGenerator
{
    /// <summary>Diagnostics: called with the tile grid and the view (x, y, width, height) as a tile graph's fill starts
    /// (the trace's <c>fillVertices</c>).</summary>
    public static Action<TileGrid, int, int, int, int>? FillStarting;

    /// <summary>Diagnostics: called with the tile grid and the view (x, y, width, height) as the level painting
    /// (<c>0x142110d60</c>) starts (compared with the trace's <c>paint.after</c>).</summary>
    public static Action<TileGrid, int, int, int, int>? PaintStarting;

    /// <summary>Diagnostics: called with a dungeon layout stage's name (as the trace's: "fill&lt;", "fill&gt;",
    /// "connect&lt;", …), the draws so far and its room-unit cells (0 free, 1 reserved, k ≥ 2 room k − 2; compared with the trace's
    /// <c>dungeon.stages</c>).</summary>
    public static Action<string, int, int[]>? DungeonStage;

    /// <summary>Diagnostics: called with the dungeon layout's rooms once each has its room (compared with the trace's
    /// <c>dungeon.rooms</c>).</summary>
    public static Action<List<string>>? DungeonRooms;

    /// <summary>Diagnostics: called with a stage name (as the trace's <c>heights</c>: "outer&lt;", "outer&gt;") and the
    /// tile grid.</summary>
    public static Action<string, TileGrid>? HeightStage;

    /// <summary>Generates the area. Pass <paramref name="rng"/> (seeded, with the area's randomness flag) to keep its draw
    /// log when generation stops at a part that is not ported, <paramref name="notes"/> to keep its notes and
    /// <paramref name="onStage"/> to see a tile graph at the stage boundaries the client trace records.</summary>
    public static GenerationResult Generate(TerrainCache files, AreaInfo area, uint seed, int topologyVariant = 60, LevelRandom? rng = null,
        List<string>? notes = null, Action<string, TileGraph>? onStage = null)
    {
        rng ??= new LevelRandom(seed, area.RandomnessDisabled) { Log = [] };
        notes ??= [];
        var features = ExtraTerrainFeatures.Roll(area, rng);
        if (features.Count > 0) notes.Add("extra terrain features: " + string.Join(", ", features.Select(f => $"{f.Name} x{f.Count}")));

        // 0x142046c60 → 0x142027a20: the area's topologies of this variant, else those of 60, else of 1.
        var choices = Choices(area.Topologies, topologyVariant, area.Id);
        // 0x142046a70: the index, then the orientation from the picked topology's list (same LCG seed).
        var (index, orientation) = rng.PickIndexAndOrientation(choices.Count, i => LevelRandom.OrientationMask(choices[i].Orientations));
        var topology = choices[index];
        notes.Add($"topology {topology.Graph} (#{index} of {choices.Count}), orientation {orientation}");

        // 0x14204a570's adds: the rolled features' rows wait in the extra room pool (generator +0x40).
        var pool = ExtraRoomPool.Create(files.Raw, features);
        bool hideout = features.Any(f => f.Name == ExtraTerrainFeatures.DiscoverableHideout);
        var secondaries = SubAreas(files, area, topology.Graph, hideout, topologyVariant, pool, rng, notes);
        var labyrinth = area.IsLabyrinth ? new LabyrinthPass(files.Raw, area.Id, area.IsLabyrinthBoss) : null;
        // A tile graph with sub-areas goes through the subgraph records too (0x142049d10), as their primary.
        bool dgr = topology.Graph.EndsWith(".dgr", StringComparison.OrdinalIgnoreCase) || secondaries.Count != 0;
        TileGrid grid;
        IReadOnlyList<DoodadRecord> records;
        if (dgr)
        {
            var tiler = new DungeonTiler(files, rng, notes, pool)
            {
                Hideout = hideout, Labyrinth = labyrinth, Secondaries = secondaries, Master = area.Master, Town = area.IsTown,
                VaalSideArea = features.Any(f => f.Name == ExtraTerrainFeatures.VaalSideArea),
            };
            grid = tiler.Generate(topology.Graph, orientation);
            records = tiler.Records;
        }
        else
        {
            var gen = new TileGraphGenerator(files, rng, notes, features.Any(f => f.Name == ExtraTerrainFeatures.VaalSideArea), onStage)
                { Hideout = hideout, Labyrinth = labyrinth, Master = area.Master, Pool = pool, Town = area.IsTown };
            grid = gen.Generate(topology.Graph, orientation);
            records = gen.Records;
        }
        return new GenerationResult(grid, rng, topology, notes, records);
    }

    static List<TopologyInfo> Choices(IReadOnlyList<TopologyInfo> topologies, int variant, string areaId)
    {
        var choices = topologies.Where(t => t.Variant == variant).ToList();
        if (choices.Count == 0) choices = topologies.Where(t => t.Variant == 60).ToList();
        if (choices.Count == 0) choices = topologies.Where(t => t.Variant == 1).ToList();
        if (choices.Count == 0) throw new InvalidOperationException($"no topologies for {areaId}");
        return choices;
    }

    /// <summary>The sub-areas' subgraphs (graph, orientation). <c>0x1420457f0</c> → <c>0x14204f590</c> on the primary
    /// graph, before the records are counted: a node description "subarea=X" (case-insensitive prefix) queues the world
    /// area X (generator +0x1f0; "Unknown sub world area"); "subarea=hideout" queues the hideout's claim subarea
    /// (Hideouts +0x2a) when the discoverable hideout rolled, else skips the node's remaining descriptions. Then
    /// <c>0x142047540</c> → <c>0x142047090</c> → <c>0x142046340</c> for each in order: its topologies (chosen as the
    /// area's), <c>Next(count)</c>, then <c>Next(840)</c> → <c>0x14299da50</c> with the picked topology's orientation
    /// mask. The node filter <c>0x142036490</c> (league= nodes) is left to <see cref="TileGraphGenerator.Prepare"/>,
    /// which stops on them.</summary>
    static List<(string Graph, int Orientation)> SubAreas(TerrainCache files, AreaInfo area, string graphPath, bool hideout, int variant,
        ExtraRoomPool pool, LevelRandom rng, List<string> notes)
    {
        var graph = TileGraph.Parse(files.Text(graphPath) ?? throw new FileNotFoundException(graphPath));
        var ids = new List<string>();
        foreach (var n in graph.Nodes)
            foreach (var d in n.Descriptions)
            {
                if (d.Length < 8 || !d.StartsWith("subarea=", StringComparison.OrdinalIgnoreCase)) continue;
                string id = d[8..];
                if (id == "hideout")
                {
                    if (!hideout) break;
                    ids.Add(area.HideoutClaim ?? throw new InvalidOperationException("Cannot find Hideout claim subarea"));
                }
                else ids.Add(id);
            }
        var result = new List<(string, int)>();
        void Add(string id, IReadOnlyList<TopologyInfo>? topologies)
        {
            var choices = Choices(topologies ?? throw new InvalidOperationException($"Unknown sub world area: {id}"), variant, id);
            var topology = choices[(int)rng.Next((uint)choices.Count)];
            int orientation = LevelRandom.OrientationFromMask(LevelRandom.OrientationMask(topology.Orientations), rng.Next(840u));
            notes.Add($"sub-area {id}: {topology.Graph}, orientation {orientation}");
            result.Add((topology.Graph, orientation));
        }
        foreach (var id in ids) Add(id, area.SubArea?.Invoke(id));
        // 0x142047540, then: each row in the extra room pool that names a world area (+0x51) adds it the same way
        // (0x1420471f0; a row with rooms passes its first room as the topology, which names no Topologies row, so the
        // world area's topologies are drawn from: HarvestEncounter's HarvestLeague, MapWorldsGorge 758818886). Rows
        // after a row's first room add records of their own (not ported), as does stat 0x13ae's sub-area (not ported).
        foreach (var row in pool.Rows)
        {
            if (row.WorldArea < 0) continue;
            if (row.Rooms.Count > 1) throw new NotSupportedException($"{row.Name}: a sub-area feature row with several rooms (0x1420471f0) is not ported");
            Add($"{row.Name}'s world area #{row.WorldArea}", area.SubAreaRow?.Invoke(row.WorldArea));
        }
        return result;
    }
}

/// <summary>A dungeon graph (.dgr) subgraph: <c>0x14203c900</c> → rooms placed (<c>0x1421038d0</c>, one node here,
/// several in <see cref="DungeonLayout"/>), tilised (<c>0x1420f3480</c>: <c>0x1420ef310</c> per room, the shared
/// border slots, the fill, <c>0x142025440</c> seams), then the outer ground (<c>0x14201db80</c>).</summary>
sealed class DungeonTiler(TerrainCache files, LevelRandom rng, List<string> notes, ExtraRoomPool pool)
{
    /// <summary>The discoverable hideout rolled (generator +0xb).</summary>
    public bool Hideout { get; init; }

    /// <summary>The area is a town (WorldAreas IsTown): the walkability pass lets an area transition tile be enclosed.</summary>
    public bool Town { get; init; }

    /// <summary>The Labyrinth's graph pass, for a Labyrinth area.</summary>
    public LabyrinthPass? Labyrinth { get; init; }

    /// <summary>A Vaal side area rolled and no graph has kept its entrance yet (generator +0xa).</summary>
    public bool VaalSideArea { get; set; }

    /// <summary>The area's TSI override (WorldAreas +0x192), the primary record's master in place of its graph's.</summary>
    public string? Master { get; init; }

    /// <summary>The sub-areas' subgraphs (graph, orientation), placed beside the primary (<c>0x142047e50</c>).</summary>
    public List<(string Graph, int Orientation)> Secondaries { get; init; } = [];

    TileGrid _grid = null!;
    TileStore _store = null!;
    Dictionary<string, List<(int Weight, string Path, int Orientation)>> _fills = null!;
    readonly List<(int X, int Y, string? Ground, short Height)> _pendingFills = [];
    int _overlap;
    bool _multi;

    /// <summary>A slot on a room's shared border (RoomOverlap, <c>0x1420eeec0</c>'s 0x80-byte entries at +0x90, by
    /// position): the key (merged when a second room reaches it), height (+0x58), the sides the rooms cover and the
    /// accessible bit (+0x5c), the side flags (+0x5d), the slot tags (+0x60) and descriptions (+0x78).</summary>
    sealed class BorderSlot
    {
        public TileKey Key = null!;
        public int Height;
        public byte Flags, Sides;
        public List<string> Tags = [];
        public List<string>? Descriptions;
    }

    readonly Dictionary<(int X, int Y), BorderSlot> _borders = [];
    EdgeCombiner? _combiner;

    /// <summary>The TileSet has "door" tiles (tiler context +0x38, <c>0x1420f0b60</c>) and the master no DisableDoors
    /// (master +0x13f; its reader was not found, the trace of 2_8_2_2 698178759 shows no door pass): rooms mark their
    /// doorways and the shared doorways get door tiles (<c>0x1420f4010</c>, <c>0x1420f4ae0</c>).</summary>
    bool _doorTiles;
    int _unit, _ox, _oy;
    (int X0, int Y0, int X1, int Y1) _view;
    /// <summary><c>0x1420ed9f0</c>'s marks (tiler +8, by position, byte +0x18): the side of its room a doorway lies on
    /// (4 top, 0x10 right, 2 bottom, 8 left).</summary>
    readonly Dictionary<(int X, int Y), byte> _doorMarks = [];
    /// <summary><c>0x1420edf80</c>: doorway positions joined into doorways (+0x48) through their doorway number per
    /// entry (+0xa8); larger door slots at +0x60.</summary>
    readonly List<List<(int X, int Y)>> _doorways = [];
    readonly List<(int X, int Y)> _bigDoors = [];
    int[] _doorwayOf = [];

    /// <summary>A subgraph (<c>0x1420457f0</c>'s 0x98-byte records, generator +0x30 → +0x10): graph (+0x20), rectangle
    /// (+0x40 x, +0x44 y, +0x48 w, +0x4c h: (unit − overlap) × the graph's size + overlap, turned with the orientation,
    /// plus the margins), master (+0x50), parent (+0x92), orientation (+0x97).</summary>
    sealed record Subgraph(string Path, int X, int Y, int W, int H, string MasterPath, Dictionary<string, string> Master,
        (int Top, int Right, int Bottom, int Left) Margins, int Orientation, int Parent);

    /// <summary>The master's OuterBufferSize (<c>0x142a06390</c>): up to four numbers, top (+0x170), right (+0x171, else
    /// top), bottom (+0x172, else top), left (+0x173, else right): the subgraph's margins.</summary>
    static (int Top, int Right, int Bottom, int Left) Margins(Dictionary<string, string> master)
    {
        var n = master.GetValueOrDefault("OuterBufferSize", "0").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        int top = n.Count > 0 ? n[0] : 0, right = n.Count > 1 ? n[1] : top, bottom = n.Count > 2 ? n[2] : top, left = n.Count > 3 ? n[3] : right;
        return (top, right, bottom, left);
    }

    /// <summary><c>0x1420457f0</c>: the record of the graph at (x, y): a .tgr's size is its graph's (+0x40 / +0x48), a
    /// .dgr's its graph's in room units. Anchoring by the orientation's corner is ported for orientation 0 only (no
    /// offset).</summary>
    Subgraph Record(string graphPath, int x, int y, int orientation, int parent, List<Subgraph>? records = null)
    {
        // The game's file lookup takes backslashes too (the Escher hideout's graph nodes name "Metadata\Terrain\...").
        graphPath = graphPath.Replace('\\', '/');
        var graph = GraphFile.Parse(files.Text(graphPath) ?? throw new FileNotFoundException(graphPath));
        // 0x14203c190: the area's TSI override, else the graph's master. A MasterFile that is no .tsi / .tgr / .dgr names
        // a group of the first record's FileGroups (master +0x268): its masters that no record uses yet (0x142050a80),
        // Next(count) picks one (MapWorldsShapersRealm's "BossArenas" and "Gauntlets", 2026-10-27).
        string masterPath = parent < 0 && Master is not null ? Master : TerrainText.Resolve(graphPath, graph.MasterFile);
        if (parent >= 0 && records is { Count: > 0 } && !graph.MasterFile.EndsWith(".tsi", StringComparison.OrdinalIgnoreCase)
            && records[0].Master.TryGetValue("FileGroups", out var fgp)
            && FileGroups(TerrainText.Resolve(records[0].MasterPath, fgp)).GetValueOrDefault(graph.MasterFile) is { } group)
        {
            var left = group.Where(m => !records.Any(r => r.MasterPath == m)).ToList();
            if (left.Count == 0) throw new InvalidOperationException("No valid master files available");
            masterPath = left[(int)rng.Next((uint)left.Count)];
        }
        var master = TerrainText.KeyValues(files.Text(masterPath) ?? throw new FileNotFoundException(masterPath));
        int unit = int.Parse(master.GetValueOrDefault("RoomUnitSize", "1")), overlap = int.Parse(master.GetValueOrDefault("RoomOverlap", "1"));
        int w = (unit - overlap) * graph.Width + overlap, h = (unit - overlap) * graph.Height + overlap;
        if (graphPath.EndsWith(".tgr", StringComparison.OrdinalIgnoreCase)) (w, h) = (graph.Width, graph.Height);
        if ((orientation & 1) != 0 && orientation < 8) (w, h) = (h, w);
        var m = Margins(master);
        if (parent >= 0 && orientation != 0) throw new NotSupportedException($"{graphPath}: subgraphs turned by orientation {orientation} (0x1420457f0's anchor) are not ported");
        return new Subgraph(graphPath, x, y, w + m.Left + m.Right, h + m.Top + m.Bottom, masterPath, master, m, orientation, parent);
    }

    /// <summary><c>0x14204bfa0</c> (each subgraph whose parent is the first, before its share of the pool rows, and the
    /// top graph with its "graph" nodes when the pool has rows): a graph's master's weight (+0x152), its master resolved as <see cref="Record"/> does
    /// (<c>0x14203c190</c> with the first record: the area's TSI override, else a FileGroups group's pick); for a .dgr,
    /// plus the weights of its "graph" nodes' graphs (<c>0x14204c520</c>: a group name stands for the group's first file
    /// in the graph's own master's FileGroups).</summary>
    int FeatureWeight(string graphPath, List<Subgraph> records, bool recurse)
    {
        graphPath = graphPath.Replace('\\', '/');
        var graph = GraphFile.Parse(files.Text(graphPath) ?? throw new FileNotFoundException(graphPath));
        string masterPath = _areaMaster ?? TerrainText.Resolve(graphPath, graph.MasterFile);
        if (_areaMaster is null && !graph.MasterFile.EndsWith(".tsi", StringComparison.OrdinalIgnoreCase)
            && records[0].Master.TryGetValue("FileGroups", out var fgp)
            && FileGroups(TerrainText.Resolve(records[0].MasterPath, fgp)).GetValueOrDefault(graph.MasterFile) is { } group)
        {
            var left = group.Where(m => !records.Any(r => r.MasterPath == m)).ToList();
            if (left.Count == 0) throw new InvalidOperationException("No valid master files available");
            masterPath = left[(int)rng.Next((uint)left.Count)];
        }
        var master = TerrainText.KeyValues(files.Text(masterPath) ?? throw new FileNotFoundException(masterPath));
        if (master.ContainsKey("ExtraFeatureWeight")) throw new NotSupportedException("ExtraFeatureWeight (master +0x152, 0x14204bfa0) is not ported");
        int weight = 0;
        if (!recurse || !graphPath.EndsWith(".dgr", StringComparison.OrdinalIgnoreCase)) return weight;
        Dictionary<string, IReadOnlyList<string>>? groups = null;
        foreach (var n in graph.Nodes)
        {
            if (n.Tag != "graph" || n.Names.Count == 0) continue;
            string file = n.Names[0];
            if (!file.EndsWith(".dgr", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".tgr", StringComparison.OrdinalIgnoreCase))
            {
                groups ??= master.TryGetValue("FileGroups", out var own) ? FileGroups(TerrainText.Resolve(masterPath, own))
                    : throw new InvalidOperationException($"No FileGroups listed in master file: {masterPath}");
                file = groups.GetValueOrDefault(file) is { Count: > 0 } g ? g[0] : throw new InvalidOperationException($"No files listed for group \"{file}\"");
            }
            weight += FeatureWeight(file, records, true);
        }
        return weight;
    }

    /// <summary>The subgraph records so far (generator +0x30), for <see cref="FeatureWeight"/>.</summary>
    List<Subgraph> _records = [];
    /// <summary>Generator +8: a dungeon record's tiling placed a tile at a height (its map view's +0x20, set by
    /// <c>0x141ffb870</c>; <c>0x14203c900</c> ORs it in). The outer ground fixes heights only then (<c>0x142044f30</c> passes it
    /// to <c>0x14201db80</c>): MapSideArea2_6, MapWorldsElder_Refinery and Menagerie_Mammals place none.</summary>
    System.Runtime.CompilerServices.StrongBox<bool> _fixHeights = new();
    /// <summary>The area's TSI override, which the walk applies to every graph (<c>0x14203c190</c> given the first record).</summary>
    string? _areaMaster;

    /// <summary>The subgraph records with their placed rooms, once generated.</summary>
    public List<DoodadRecord> Records { get; private set; } = [];

    /// <summary>The rooms <see cref="Tilise"/> placed, in layout order.</summary>
    List<PlacedRoom> _placed = [];

    /// <summary>Every .dgr goes through the subgraph records, a single room's too: the client sizes its map as the record,
    /// (unit − overlap) × the graph's size + overlap plus the margins, and fills its outer buffer.</summary>
    public TileGrid Generate(string graphPath, int graphOrientation) => Composite(graphPath, graphOrientation);

    /// <summary><c>0x142049d10</c>: the subgraph records in order, each laid out and tilised on one tile map
    /// (<c>0x1420452e0</c> → <c>0x14203c900</c>; a layout's graph nodes add their subgraphs, <c>0x14204f030</c>); then,
    /// last subgraph first, each one's outer ground and outer buffer (<c>0x142044f30</c>); then StoreyHeight. A .tgr
    /// subgraph is laid out on the same map at its view (<c>0x14203f890</c>, <see cref="TileGraphGenerator.Subgraph"/>);
    /// the first record (generator +0x30 +9, taken to be 1) is the primary one.</summary>
    TileGrid Composite(string graphPath, int graphOrientation)
    {
        var records = new List<Subgraph> { Record(graphPath, 0, 0, graphOrientation, -1) };
        _records = records;
        _areaMaster = Master;
        var (gw, gh) = PlaceSecondaries(records);
        var grid = new TileGrid(gw, gh) { CheckGrounds = false, GroundFlags = g => files.Ground(g)?.Flags ?? 0, Unmirrored = files.Unmirrored };
        var outers = new List<Action>();
        var roomsOf = new List<IReadOnlyList<PlacedRoom>>();
        // 0x142049d10: with rows in the pool, the weight to share them by is the sum of the subgraphs' masters' +0x152
        // (0x14204bfa0); +0x152 is taken to default to 0 (no trace drew 0x141d8bba0's remainder roll), and a master
        // that sets ExtraFeatureWeight (the key taken to be +0x152) is not ported. The walk resolves every master as a
        // record would, so a FileGroups master draws its pick here too.
        if (pool.Rows.Count != 0) FeatureWeight(records[0].Path, records, true);
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            notes.Add($"subgraph {i}: {r.Path} at ({r.X}, {r.Y}) {r.W}x{r.H}");
            int vx = r.X + r.Margins.Left, vy = r.Y + r.Margins.Top, vw = r.W - r.Margins.Left - r.Margins.Right, vh = r.H - r.Margins.Top - r.Margins.Bottom;
            TileStore store;
            if (r.Path.EndsWith(".tgr", StringComparison.OrdinalIgnoreCase))
            {
                // 0x14203f890: a subgraph whose parent is the first (+0x92 = 0) takes its share of the rows. The tile
                // graph's special nodes do not take them (not ported).
                if (r.Parent <= 0) pool.Spread(FeatureWeight(r.Path, records, false), rng);
                if (pool.Share != 0) throw new NotSupportedException($"{r.Path}: extra feature rooms in a tile graph subgraph are not ported");
                // The rolled Vaal side area goes to the first graph with side area nodes (2_9_4's oasis_foothills_4.tgr).
                var gen = new TileGraphGenerator(files, rng, notes, VaalSideArea)
                    { Hideout = Hideout, Labyrinth = Labyrinth, Master = r.Parent < 0 ? Master : null, Town = Town, PreparePool = pool };
                var (_, _, tgFills) = gen.Subgraph(r.Path, r.Orientation, grid, vx, vy, primary: i == 0);
                if (gen.SideAreaKept) VaalSideArea = false;
                var tgStore = store = gen.Store;
                roomsOf.Add(gen.Rooms);
                outers.Add(() => OuterTileGraph(r, grid, tgStore, tgFills));
            }
            else
            {
                var t = i == 0 ? this : new DungeonTiler(files, rng, notes, pool) { Hideout = Hideout, Labyrinth = Labyrinth, Town = Town, _records = records, _areaMaster = _areaMaster, _fixHeights = _fixHeights };
                t.Tilise(r, grid, records, i);
                store = t._store;
                roomsOf.Add(t._placed);
                outers.Add(() => t.Outer(r));
            }
            // 0x1420452e0 after the layout: the TileSet's post-unique tiles over the subgraph's view (0x14203ece0).
            PostUniqueTiles.Run(rng, grid, vx, vy, vw, vh, store, notes);
        }
        LevelGenerator.HeightStage?.Invoke("outer<", grid);
        for (int i = records.Count - 1; i >= 0; i--) outers[i]();
        LevelGenerator.HeightStage?.Invoke("outer>", grid);
        // 0x142044f30 then: the walkability pass (0x1420289a0) over the rectangle of each primary record (generator
        // +0x30 +9: the first and the queued sub-areas, MapWorldsGraveyard's hideout claim; not the subgraphs graph nodes
        // add during the layout, 2026-10-27) and of each later one whose master has CountAsPrimary (+0x164), then
        // 0x142028870 flags the map's border vertices. Only grounds and vertex flags change.
        for (int i = 0; i < records.Count; i++)
            if (i <= Secondaries.Count || records[i].Master.ContainsKey("CountAsPrimary"))
                new GroundStages(grid, records[i].X, records[i].Y, records[i].W, records[i].H, files).Walkability(TileGraphGenerator.ImpassableGround, files.HasTransition, Town);
        new GroundStages(grid, 0, 0, grid.Width, grid.Height, files).FlagBorder();
        // 0x14204cbd0: tile heights in levels become the client's units, each tile origin by the StoreyHeight of the
        // last record whose rectangle holds it (0x141d77a90), else the first's.
        var storeys = records.Select(r => int.Parse(r.Master.GetValueOrDefault("StoreyHeight", "0"))).ToList();
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
                if (grid[x, y] is { IsOrigin: true, HeightOffset: not 0 })
                {
                    int k = records.FindLastIndex(r => x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H);
                    grid.ScaleHeight(x, y, storeys[Math.Max(k, 0)]);
                }
        Records = [.. records.Select((r, i) => new DoodadRecord(r.X, r.Y, r.W, r.H, r.Master, r.MasterPath, roomsOf[i]))];
        return grid;
    }

    /// <summary><c>0x142047e50</c>: each secondary record (parent +0x92 = its index) goes to the corner, among those
    /// right of (x + w + 1, y) and below (x, y + h + 1) each earlier record, that keeps a tile clear of every placed
    /// rectangle (<c>0x1420487f0</c>) and gives the smallest bounding area: max(threshold, map width, its right) ×
    /// max(threshold, map height, its bottom), the first of equals (a stable sort). The threshold is 0, or with more
    /// than 10 records 4/5 of the integer square root of their total area (<c>0x141937840</c>). Returns the map's size
    /// (<c>gen[5]</c> vtable +8).</summary>
    (int W, int H) PlaceSecondaries(List<Subgraph> records)
    {
        for (int i = 0; i < Secondaries.Count; i++)
            records.Add(Record(Secondaries[i].Graph, 0, 0, Secondaries[i].Orientation, i + 1));
        int threshold = records.Count > 10 ? (int)Math.Sqrt(records.Sum(r => r.W * r.H)) * 4 / 5 : 0;
        var placed = new List<(int X0, int Y0, int X1, int Y1)> { (records[0].X, records[0].Y, records[0].X + records[0].W, records[0].Y + records[0].H) };
        int mw = records[0].W, mh = records[0].H;
        for (int i = 1; i < records.Count; i++)
        {
            var r = records[i];
            var candidates = new List<(long Score, int X, int Y)>();
            void Try(int cx, int cy)
            {
                if (placed.Any(p => p.X0 < cx + r.W + 1 && cx - 1 < p.X1 && p.Y0 < cy + r.H + 1 && cy - 1 < p.Y1)) return;
                long w = Math.Max(threshold, Math.Max(mw, cx + r.W)), h = Math.Max(threshold, Math.Max(mh, cy + r.H));
                candidates.Add((w * h, cx, cy));
            }
            for (int j = 0; j < i; j++)
            {
                Try(records[j].X + records[j].W + 1, records[j].Y);
                Try(records[j].X, records[j].Y + records[j].H + 1);
            }
            var best = candidates.OrderBy(c => c.Score).First();
            records[i] = r = r with { X = best.X, Y = best.Y };
            placed.Add((r.X, r.Y, r.X + r.W, r.Y + r.H));
            mw = Math.Max(mw, r.X + r.W);
            mh = Math.Max(mh, r.Y + r.H);
        }
        return (mw, mh);
    }

    /// <summary><c>0x14203c900</c> for one subgraph of several nodes on the shared map: the layout, at the record's
    /// corner inside its margins; the rooms tilised; then each room without a room but with descriptions (a graph
    /// node's, <c>0x14204f030</c> → <c>0x1420468e0</c>) adds the subgraph its first description names, at the room's
    /// corner (orientation 0: no offset).</summary>
    void Tilise(Subgraph r, TileGrid grid, List<Subgraph> records, int index)
    {
        var master = r.Master;
        string masterPath = r.MasterPath;
        _grid = grid;
        // A master without a TileSet (CombinedArena's, whose graph holds only subgraph nodes) has no tiles.
        _store = new TileStore(files, master.TryGetValue("TileSet", out var tst) ? files.TileList(TerrainText.Resolve(masterPath, tst)) : []);
        _fills = master.TryGetValue("FillTiles", out var gft) ? files.FillTiles(TerrainText.Resolve(masterPath, gft)) : [];
        int unit = int.Parse(master.GetValueOrDefault("RoomUnitSize", "1"));
        int raised = grid.RaisedPlacements;
        var map = Layout(r.Path, r.Orientation, masterPath, master, unit, index == 0, r.Parent <= 0);
        // Room k starts at the view's corner plus (unit − overlap) × its cell.
        int step = unit - map.Overlap;
        _ox = r.X + r.Margins.Left;
        _oy = r.Y + r.Margins.Top;
        // The tiler's view (0x1420f3480 +8, which its fill keeps): the subgraph's rectangle inside its margins.
        _view = (_ox, _oy, r.X + r.W - r.Margins.Right, r.Y + r.H - r.Margins.Bottom);
        _record = (r.X, r.Y, r.X + r.W, r.Y + r.H);
        if (_ox < 0 || _oy < 0 || _ox + step * map.Width + map.Overlap > grid.Width || _oy + step * map.Height + map.Overlap > grid.Height)
            throw new InvalidOperationException($"Subgraph {r.Path} extends outside the TileMap!");
        _overlap = map.Overlap;
        _multi = true;
        _unit = unit;
        _doorTiles = _store.HasTag("door") && master.GetValueOrDefault("DisableDoors", "0") == "0";
        _doorwayOf = new int[_grid.Width * _grid.Height];
        if (master.TryGetValue("EdgeCombination", out var ecf))
            _combiner = EdgeCombiner.Parse(files.Text(TerrainText.Resolve(masterPath, ecf)) ?? "", masterPath);
        if (master.TryGetValue("FillTiles", out var edgeGft)) _edgePoints = files.FillEdgeTypes(TerrainText.Resolve(masterPath, edgeGft));
        // Without RoomOverlap, a master's DoorEdgeType (+0x250) gives the door tiles (0x1420edd50 / 0x1420f4d90).
        string? doorEdge = _overlap == 0 && _doorTiles && master.TryGetValue("DoorEdgeType", out var det) ? TerrainText.Resolve(masterPath, det) : null;
        _doorSides.Clear();
        // 0x1420f3480: 0x1420ef310 per room.
        foreach (var p in map.Rooms)
        {
            if (p.Room?.Def is not { } def) continue;
            if (_doorTiles && _overlap != 0) MarkDoorways(p, _ox + step * p.X, _oy + step * p.Y);
            else if (doorEdge is not null) MarkDoorSides(p, _ox + step * p.X, _oy + step * p.Y);
            TiliseRoom(def, p.Orientation, _ox + step * p.X, _oy + step * p.Y, p.Level, (p.Flags & 1) != 0, p.Descriptions);
        }
        notes.Add($"dungeon tilising: {_borders.Count} shared border slots");
        TiliseBorders();
        // With RoomOverlap (and master +0x167 clear, taken to be so) the door tiles; without, 0x1420f4d90 for a master
        // with a DoorEdgeType (+0x250, the setter after OuterEdgeType's +0x248; the dreadnought hideout and the Lake
        // masters). 0x1420f7960 (OuterEdgeType) is not ported. Then 0x141ffbd70 per room: its inner vertex grounds,
        // turned like a tile's.
        if (_overlap != 0 && _doorTiles)
        {
            DoorTiles();
            BigDoorTiles();
        }
        else if (doorEdge is not null) DoorEdgeTiles(doorEdge);
        var roomVertices = new HashSet<(int X, int Y)>();
        foreach (var p in map.Rooms)
            if (p.Room?.Def is { VertexGrounds.Length: > 0 } def)
                for (int v = 1; v < def.Height; v++)
                    for (int u = 1; u < def.Width; u++)
                        if (def.VertexGrounds[(v - 1) * (def.Width - 1) + u - 1] is { } g)
                        {
                            var (dx, dy) = Orientation.MapVertex(p.Orientation, u, v, def.Width, def.Height);
                            _grid.ForceGround(_ox + step * p.X + dx, _oy + step * p.Y + dy, g);
                            roomVertices.Add((_ox + step * p.X + dx, _oy + step * p.Y + dy));
                        }
        // Map +0x10 is off while the rooms' grounds go in (0x1420f3480).
        bool checkGrounds = _grid.CheckGrounds;
        _grid.CheckGrounds = false;
        RoomGrounds(map, step);
        _grid.CheckGrounds = checkGrounds;
        var placed = _placed = [];
        foreach (var p in map.Rooms)
            if (p.Room?.Def is { } def)
            {
                var (w, h) = Orientation.Size(p.Orientation, def.Width, def.Height);
                placed.Add(new PlacedRoom(def, _ox + step * p.X, _oy + step * p.Y, w, h, p.Descriptions, p.Orientation));
            }
        var overlayRooms = FillRecords(master, masterPath, placed, r.Parent <= 0);
        Wiggle();
        // The mirror seams (0x142025440) over the layout's view, with the placed rooms as its room list.
        new Seams(files, rng, _grid, _ox, _oy, step * map.Width + map.Overlap, step * map.Height + map.Overlap, _store, placed, roomVertices,
            _graphGrounds, notes).Run();
        // The overlays' rooms, the fill's rooms (.gft lines naming an .arm) and the rooms injected into mission tiles join
        // the record's rooms after the layout's, for the doodad stage.
        placed.AddRange(overlayRooms);
        placed.AddRange(_fillRooms);
        placed.AddRange(_injectedRooms);
        if (grid.RaisedPlacements != raised) _fixHeights.Value = true;
        foreach (var p in map.Rooms)
            if (p.Room is null && p.Descriptions.Count != 0)
                records.Add(Record(p.Descriptions[0], _ox + step * p.X, _oy + step * p.Y, r.Orientation, index, records));
    }

    /// <summary><c>0x142044f30</c> for a .tgr subgraph on the shared map: its outer ground (<c>0x14201db80</c>, a tile
    /// graph's: no height fixing) over its view, then its outer buffer (<c>0x14201ffa0</c>) in its margins, with the tile
    /// graph's TileSet and FillTiles.</summary>
    void OuterTileGraph(Subgraph r, TileGrid grid, TileStore store, Dictionary<string, List<(int Weight, string Path, int Orientation)>> fills)
    {
        var view = (r.X + r.Margins.Left, r.Y + r.Margins.Top, r.X + r.W - r.Margins.Right, r.Y + r.H - r.Margins.Bottom);
        if (r.Master.TryGetValue("OuterGroundType", out var outerGround))
        {
            var ground = files.Ground(TerrainText.Resolve(r.MasterPath, outerGround)) ?? throw new FileNotFoundException(outerGround);
            OuterGroundStart(grid, OuterTile(files, fills, ground), view, fixHeights: false);
        }
        if (r.Margins == (0, 0, 0, 0)) return;
        new OuterBuffer(files, rng, new OuterBuffer.View(grid, r.X, r.Y, r.W, r.H), view.Item1 - r.X, view.Item2 - r.Y,
            view.Item3 - r.X, view.Item4 - r.Y, store, fills, notes).Run();
    }

    /// <summary><c>0x142044f30</c> for one subgraph: its outer ground (<c>0x14201db80</c>) over its view, then its outer
    /// buffer (<c>0x14201ffa0</c>) in its margins, on the view of its record's rectangle (<c>0x141ffb7f0</c>).</summary>
    void Outer(Subgraph r)
    {
        _view = (r.X + r.Margins.Left, r.Y + r.Margins.Top, r.X + r.W - r.Margins.Right, r.Y + r.H - r.Margins.Bottom);
        OuterGround(r.Master, r.MasterPath);
        if (r.Margins == (0, 0, 0, 0)) return;
        new OuterBuffer(files, rng, new OuterBuffer.View(_grid, r.X, r.Y, r.W, r.H), _view.X0 - r.X, _view.Y0 - r.Y,
            _view.X1 - r.X, _view.Y1 - r.Y, _store, _fills, notes).Run();
    }

    /// <summary>The tag of a Sanctum floor's rooms when the server sends none: SanctumRooms row 0's room
    /// (Cellar_Explore_1_1, Id +0, ArmFile +8), on every floor. Null when no SanctumRooms row lies beside
    /// <paramref name="masterPath"/> (not a Sanctum floor).</summary>
    string? SanctumRoomTag(string masterPath)
    {
        if (files.Raw("Data/SanctumRooms.datc64") is not { } data) return null;
        var rooms = new RawDat(data);
        string folder = masterPath[..(masterPath.LastIndexOf('/') + 1)];
        if (rooms.Find(r => rooms.String(r, 8).StartsWith(folder, StringComparison.OrdinalIgnoreCase)) < 0) return null;
        string arm = rooms.String(0, 8);
        return (files.Room(arm) ?? throw new FileNotFoundException(arm)).Tag;
    }

    /// <summary><c>0x14203c900</c> for a graph of several nodes: the exist rolls (as for tile graphs), then the rooms
    /// on a grid of the graph's size in room units (<c>0x1421038d0</c>).</summary>
    DungeonMap Layout(string graphPath, int graphOrientation, string masterPath, Dictionary<string, string> master, int unit, bool primary,
        bool share = true)
    {
        var graph = TileGraph.Parse(files.Text(graphPath) ?? throw new FileNotFoundException(graphPath));
        // A Sanctum floor's .dgr is one node tagged "land"; the client rebuilds the first graph (0x141f5e790): 6 × 4
        // cells of 23 units, a node at cell (0, 0) with "default" for the server's first room and one at (4, 2) for its
        // second (0x141f5ea10: the room's .arm tag, any orientation). Without server data both are SanctumRooms row 0,
        // and every floor fails as the client does: the Crypt has no room of that tag ("No possible rooms found for room
        // at (0, 0)"), the others find no free cells for the second ("No suitable room found for room at (4, 2)").
        if (primary && graph.Nodes.Any(n => n.Tag == "land") && SanctumRoomTag(masterPath) is { } land)
        {
            (graph.Width, graph.Height) = (6, 4);
            graph.Nodes = [new GraphNode { Tag = land, Descriptions = ["default"] }, new GraphNode { X = 4 * 23, Y = 2 * 23, Tag = land }];
        }
        // 0x142031d10: the Vaal side area pick, as on tile graphs; the first graph that keeps an entrance clears it.
        if (TileGraphGenerator.Prepare(graph, rng, VaalSideArea, Hideout, primary, pool, Labyrinth)) VaalSideArea = false;
        // Then a subgraph whose parent is the first (+0x92 = 0) takes its share of the rows, the side area's included.
        if (share) pool.Spread(FeatureWeight(graphPath, _records, false), rng);
        string? Ground(string g) => g.Length == 0 ? null : TerrainText.Resolve(graphPath, g);
        _graphGrounds = (Ground(graph.TopGround), Ground(graph.BottomGround), Ground(graph.DefaultGround));
        notes.Add($"dungeon graph {graphPath}: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges after exist rolls");
        var roomSet = RoomSetOf(master, masterPath);
        var map = new DungeonMap(graph.Width, graph.Height, unit, int.Parse(master.GetValueOrDefault("RoomOverlap", "1")));
        var swap = (master.TryGetValue("SwapDungeonDoorsFrom", out var from) ? TerrainText.Resolve(masterPath, from) : null,
            master.TryGetValue("SwapDungeonDoorsTo", out var to) ? TerrainText.Resolve(masterPath, to) : null);
        var groups = master.TryGetValue("FileGroups", out var fgp) ? FileGroups(TerrainText.Resolve(masterPath, fgp)) : null;
        // Master +0x159 / +0x14d: the fill rooms' fixed level (0x142108f70), taken to be set by the key.
        sbyte? fillLevel = master.TryGetValue("DungeonFillRoomHeightOverride", out var fh) ? (sbyte)byte.Parse(fh.Trim()) : null;
        var outerGround = master.TryGetValue("OuterGroundType", out var og) ? TerrainText.Resolve(masterPath, og) : null;
        new DungeonLayout(files, rng, notes, graph, roomSet, map, graphOrientation, swap, LevelGenerator.DungeonStage,
            groups is null ? null : g => groups.GetValueOrDefault(g), outerGround, pool, fillLevel).Run();
        return map;
    }

    /// <summary>The master's RoomSet; a master without one (MapOba's top master.tsi, whose graph holds only subgraph
    /// nodes) has no rooms.</summary>
    DungeonRoomSet RoomSetOf(Dictionary<string, string> master, string masterPath) =>
        new(files, master.TryGetValue("RoomSet", out var rs) ? TerrainText.Resolve(masterPath, rs) : null);

    /// <summary>A FileGroups file (.fgp, master +0x268, <c>0x141fe83e0</c>): after the version line, a quoted group name,
    /// then its files (relative to the .fgp) until the next name.</summary>
    Dictionary<string, IReadOnlyList<string>> FileGroups(string path)
    {
        var groups = new Dictionary<string, IReadOnlyList<string>>();
        List<string>? current = null;
        foreach (var line in TerrainText.Lines(files.Text(path) ?? throw new FileNotFoundException(path)))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("version", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith('"')) groups[t.Trim('"')] = current = [];
            else current?.Add(TerrainText.Resolve(path, t));
        }
        return groups;
    }


    // 0x141d75980.
    static readonly byte[] ComposeTable =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 0,  1, 2, 3, 0, 7, 4, 5, 6, 1,  2, 3, 0, 1, 6, 7, 4, 5, 2,
        3, 0, 1, 2, 5, 6, 7, 4, 3,  4, 5, 6, 7, 0, 1, 2, 3, 4,  5, 6, 7, 4, 3, 0, 1, 2, 5,
        6, 7, 4, 5, 2, 3, 0, 1, 6,  7, 4, 5, 6, 1, 2, 3, 0, 7,  8, 8, 8, 8, 8, 8, 8, 8, 8,
    ];

    internal static int Compose(int a, int b) => ComposeTable[Math.Min(a, 8) * 9 + Math.Min(b, 8)];

    /// <summary><c>0x1420ef310</c>: every 'k' slot of the room, row by row. A slot without edges, tag or heights whose
    /// corners share one ground is left for the fill (<c>0x1420eec10</c>); any other picks a tile from the store.</summary>
    void TiliseRoom(RoomDef room, int code, int originX, int originY, int level = 0, bool accessible = false, List<string>? descriptions = null)
    {
        var (roomW, roomH) = Orientation.Size(code, room.Width, room.Height);
        for (int row = 0; row < room.Height; row++)
            for (int col = 0; col < room.Width; col++)
            {
                var slot = room.Slots[col, row];
                if (slot.Kind is not ('k' or 'f' or 's') || slot.Key is null) continue;
                var key = slot.Key;
                var (w, h) = Orientation.Size(code, key.Width, key.Height);
                var (x, y) = SlotOrigin(code, col + slot.ShiftX, row + slot.ShiftY, key.Width, key.Height, room.Width, room.Height);
                x += originX;
                y += originY;
                // Slot height (+0x72) plus the room's level (+0x12).
                short height = (short)(slot.Height + level);
                if (slot.Tag.Length == 0 && key.Edges.All(e => e is null) && key.Heights.All(v => v == 0) &&
                    key.Grounds.Distinct().Count() == 1)
                {
                    if (_multi)
                    {
                        PendingFill(x, y, key.Rotate(code), height, accessible);
                        continue;
                    }
                    for (int dy = 0; dy < h; dy++)
                        for (int dx = 0; dx < w; dx++)
                            _pendingFills.Add((x + dx, y + dy, key.Grounds[0], height));
                    continue;
                }
                // 0x1420f0a70, after the plain test (a plain slot goes to the fill even on the room's outer row): with
                // RoomOverlap, a key of odd size centred on the room's outer row or column is shared with the neighbour
                // (not for "missiontile").
                int lx = x - originX, ly = y - originY;
                int bx = lx < 1 ? 1 : roomW <= lx + w ? 2 : 0, by = ly < 1 ? 1 : roomH <= ly + h ? 2 : 0;
                bool shared = _overlap != 0 && slot.Tag != "missiontile" &&
                    ((bx == 1 && w % 2 == 1 && lx + w / 2 == 0) || (bx == 2 && w % 2 == 1 && lx + w / 2 == roomW - 1) ||
                     (by == 1 && h % 2 == 1 && ly + h / 2 == 0) || (by == 2 && h % 2 == 1 && ly + h / 2 == roomH - 1));
                // An untagged square slot whose edges all have its size and one a wiggle of at least 2 (1 when shared)
                // is noted for 0x1420f8f20.
                if (slot.Tag.Length == 0 && Wiggles(key.Rotate(code), shared ? 1 : 2)) NoteWiggle(x - _view.X0, y - _view.Y0);
                if (shared)
                {
                        Border(x, y, bx, by, height, key.Rotate(code), slot.Tag, accessible, descriptions);
                    continue;
                }
                var list = _store.Candidates(slot.Tag, key.Rotate(code));
                if (list.Count == 0 && slot.Tag == "missiontile")
                {
                    // 0x1420ef310: a mission tile without a tile takes an injected room of its size (0x141d8b450).
                    var injected = pool.Take(w, h, rng) ?? throw new NotSupportedException($"{room.Path}: a mission tile without an injected room is not ported");
                    TiliseInjected(injected.Room.Def!, injected.Orientation, x, y, height);
                    continue;
                }
                if (list.Count == 0) throw new InvalidOperationException($"{room.Path}: no tile for slot ({col}, {row}) at ({x}, {y}) {key.Rotate(code)} tag '{slot.Tag}'");
                var (tile, orientation) = Weighted(list, (int)rng.Next((uint)list.Sum(c => c.Weight)));
                // 0x1420ef310 places the tile (0x141ffb870), then sets its height (0x141d82d00): its vertices take the
                // slot height plus the tile's own, whatever grounds they have.
                _grid.Place(tile, orientation, x, y, height);
                _grid.SetHeight(x, y, height);
                RecordEdgeTypes(key, x, y);
            }
    }

    /// <summary><c>0x1420f8df0</c>: a placed tile whose key has an edge type of the FillTiles object's +0x140 list on one to
    /// three sides is recorded (tiler +0x40) once per such list entry, for the edge fill (<c>0x141feed20</c>).</summary>
    void RecordEdgeTypes(TileKey key, int x, int y)
    {
        for (int i = 0; i < _edgePoints.Count; i++)
        {
            int n = key.Edges.Count(e => string.Equals(e, _edgePoints[i].Edge, StringComparison.OrdinalIgnoreCase));
            if (n is >= 1 and <= 3) _edgeRecords.Add((x, y, i));
        }
    }

    // 0x141fe89e0's offset of a slot's tile by (1 − w, 1 − h) per orientation, as 0x141fea780's (SpecialNodes).
    static readonly int[] SlotShift = [0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0];

    /// <summary><c>0x1420ef310</c>'s mission tile at (x, y): its w × h vertices take the slot height (flag 2), then
    /// <c>0x141fe89e0</c> tilises the injected room there: every 'k' / 'f' slot column by column, whose entry is still
    /// empty, takes a tile of its tag and key (<c>Next(total)</c>, up to five tries until it fits, <c>0x141ffbbe0</c>) at
    /// the slot height plus the room's; then the room's vertex grounds, and every vertex of the room still without a
    /// ground takes the ground at the mission tile's corner. A plain slot (no tag or edges, one ground) on an empty entry
    /// first tries a fill tile of the master's FillTiles for its ground, as a fill room's (<see cref="Fill.FillSlot"/>).
    /// Ground checks (map +0x10) are off while the slots are tiled. Nested mission tiles and the pool's own tiles
    /// (+0x90) are not ported.</summary>
    void TiliseInjected(RoomDef room, int o, int x, int y, int height)
    {
        var (w, h) = Orientation.Size(o, room.Width, room.Height);
        _injectedRooms.Add(new PlacedRoom(room, x, y, w, h, [], o));
        for (int v = 0; v < h; v++)
            for (int u = 0; u < w; u++)
            {
                ref var c = ref _grid.Corner(x + u, y + v);
                c.Height = (sbyte)height;
                c.Flags |= 2;
            }
        _grid.MarkHeights(height != 0);
        string? ground = _grid.Corner(x, y).Ground;
        bool check = _grid.CheckGrounds;
        _grid.CheckGrounds = false;
        var fill = new Fill(files, rng, _grid, 0, 0, _grid.Width, _grid.Height, _store, _fills, [], notes);
        for (int col = 0; col < room.Width; col++)
            for (int row = 0; row < room.Height; row++)
            {
                var slot = room.Slots[col, row];
                if (slot is null || slot.Kind is not ('k' or 'f' or 's') || slot.Key is null) continue;
                var (gx, gy) = Orientation.Map(o, col, row, room.Width, room.Height);
                int sw = 1 - slot.Key.Width, sh = 1 - slot.Key.Height, so = Math.Min(o, 7) * 4;
                int tx = x + gx + sw * SlotShift[so] + sh * SlotShift[so + 2];
                int ty = y + gy + sw * SlotShift[so + 1] + sh * SlotShift[so + 3];
                var key = slot.Key.Rotate(o);
                if (Fill.IsFillSlot(slot) && _fills.Count != 0)
                {
                    if (_grid[tx, ty] is not null) continue;
                    if (fill.FillSlot(room, col, row, o, x + gx, y + gy, (short)(slot.Height + height)) is int th)
                    {
                        row += th - 1;
                        continue;
                    }
                }
                var list = _store.Candidates(slot.Tag, key);
                if (list.Count == 0) throw new NotSupportedException($"{room.Path}: slot ({col}, {row}) {key} tag '{slot.Tag}' has no tile (mission tiles and the pool's tiles are not ported here)");
                if (_grid[tx, ty] is not null) continue;
                for (int tries = 0; ; tries++)
                {
                    if (tries > 4) throw new InvalidOperationException($"No tile could fit at ({tx}, {ty}) for the fill room here");
                    var (tile, to) = Weighted(list, (int)rng.Next((uint)list.Sum(c => c.Weight)));
                    var (tw, th) = Orientation.Size(to, tile.Width, tile.Height);
                    bool fits = true;
                    for (int v = 0; v < th && fits; v++)
                        for (int u = 0; u < tw && fits; u++)
                            fits = _grid.Inside(tx + u, ty + v) && _grid[tx + u, ty + v] is null;
                    if (!fits) continue;
                    _grid.Place(tile, to, tx, ty, (short)(slot.Height + height));
                    break;
                }
            }
        if (room.VertexGrounds.Length > 0)
            for (int v = 1; v < room.Height; v++)
                for (int u = 1; u < room.Width; u++)
                    if (room.VertexGrounds[(v - 1) * (room.Width - 1) + u - 1] is { } g)
                    {
                        var (dx, dy) = Orientation.MapVertex(o, u, v, room.Width, room.Height);
                        _grid.ForceGround(x + dx, y + dy, g);
                    }
        _grid.CheckGrounds = true;
        for (int v = 0; v <= h; v++)
            for (int u = 0; u <= w; u++)
                if (_grid.Corner(x + u, y + v).Ground is null) _grid.SetGround(x + u, y + v, ground);
        _grid.CheckGrounds = check;
    }

    /// <summary><c>0x1420eeec0</c>: the first room at a shared border position stores its slot; the sides it covers
    /// (all but the one outside it) and the accessible bit go to +0x5c, the side it lies on (not at a corner) to +0x5d.
    /// A second room's slot is merged into it (<c>0x1420ee240</c>); the key's heights then start at 0 again, the
    /// lowest going to the slot's height.</summary>
    void Border(int x, int y, int bx, int by, int height, TileKey key, string tag, bool accessible, List<string>? descriptions)
    {
        byte flags = (byte)((accessible ? 1 : 0) | (bx != 2 ? 0x10 : 0) | (bx != 1 ? 8 : 0) | (by != 2 ? 2 : 0) | (by != 1 ? 4 : 0));
        if (_borders.TryGetValue((x, y), out var b))
        {
            b.Key = Merge(b, key, height, flags, x, y);
            int low = b.Key.Heights.Min();
            for (int i = 0; i < 4; i++) b.Key.Heights[i] -= (sbyte)low;
            b.Height += low;
            b.Flags |= flags;
        }
        else _borders[(x, y)] = b = new BorderSlot { Key = key, Height = height, Flags = flags };
        if (tag.Length != 0 && !b.Tags.Contains(tag)) b.Tags.Add(tag);
        if (descriptions is { Count: > 0 }) b.Descriptions = b.Descriptions is null ? descriptions : [];
        b.Sides |= (byte)((bx, by) switch { (1, 0) => 0x10, (2, 0) => 8, (0, 1) => 2, (0, 2) => 4, _ => 0 });
    }

    /// <summary><c>0x1420f6b70</c>: the shared border slots by (x, y). A slot without tags whose key is plain
    /// (<c>0x1420ee9e0</c>) is left for the fill (<c>0x1420eec10</c>). Otherwise its footprint must be all free or all
    /// taken ("partial overlap"); all taken, the tile there merges into the key; all free, unless it lies on no side
    /// (+0x5d has all of 0x1e), the sides it covers but does not lie on are merged with the key without its edges
    /// (<c>0x1420ee240</c>): each edge there meets no edge, which the EdgeCombination may turn into another or none
    /// (the temple's walkway + "" is none: a walkway along a room's shared column ends). Its tags are tried in
    /// <c>Next(total)</c> order, each weighted by its store total (1 without tiles), until one places
    /// (<c>0x1420f5fb0</c>); else a key that is not plain must place untagged ("Could not find matching overlap tile"),
    /// a plain one goes to the fill.</summary>
    void TiliseBorders()
    {
        foreach (var ((x, y), b) in _borders.OrderBy(e => e.Key.X).ThenBy(e => e.Key.Y))
        {
            var key = b.Key;
            if (b.Tags.Count == 0 && Plain(key))
            {
                PendingFill(x, y, key, b.Height, (b.Flags & 1) != 0);
                continue;
            }
            int taken = 0;
            for (int v = 0; v < key.Height; v++)
                for (int u = 0; u < key.Width; u++)
                    if (_grid.Inside(x + u, y + v) && _grid[x + u, y + v] is not null) taken++;
            if (taken != 0 && taken != key.Width * key.Height)
                throw new InvalidOperationException($"Failed to create overlap tile at ({x}, {y}) because of partial overlap");
            if (taken != 0)
            {
                // A tile already there (an earlier layout's room): its key, turned by its orientation, merges into the
                // slot's over the sides the slot's rooms leave (0x1e less the covered ones: 0x1c and 0x1a, 0x16 and 0xe
                // swap, else all), at its height offset; the key's heights start at 0 again.
                var e = _grid[x, y]!.Value;
                byte covered = (byte)(b.Flags & 0x1e);
                byte other = covered switch { 0x1c => 0x1a, 0x1a => 0x1c, 0x16 => 0x0e, 0x0e => 0x16, _ => 0x1e };
                b.Key = Merge(b, e.Tile.Key.Rotate(e.Orientation), e.HeightOffset, other, x, y);
                int low = b.Key.Heights.Min();
                for (int i = 0; i < 4; i++) b.Key.Heights[i] -= (sbyte)low;
                b.Height += low;
                b.Flags |= other;
                key = b.Key;
            }
            else if ((b.Sides & 0x1e) != 0x1e)
            {
                byte own = (byte)(~b.Sides & b.Flags & 0x1e);
                var self = key.Clone();
                Array.Clear(self.Edges);
                b.Key = Merge(b, self, b.Height, own, x, y);
                key = b.Key;
            }
            var tags = b.Tags.Select(t => (Tag: t, Weight: Math.Max(1, _store.Candidates(t, key).Sum(c => c.Weight)))).ToList();
            bool placed = false;
            while (tags.Count != 0 && !placed)
            {
                int r = (int)rng.Next((uint)tags.Sum(t => t.Weight)), i = 0;
                while (i < tags.Count - 1 && r >= tags[i].Weight) r -= tags[i++].Weight;
                placed = PlaceOverlap(key, tags[i].Tag, x, y, b.Height);
                // A 1 × 1 slot placed with the tag "door" is a doorway position.
                if (placed && key.Width == 1 && key.Height == 1 && tags[i].Tag == "door") AddDoorway(x, y, 1, 1);
                if (!placed) tags.RemoveAt(i);
            }
            if (placed) continue;
            if (Plain(key))
            {
                PendingFill(x, y, key, b.Height, (b.Flags & 1) != 0);
                continue;
            }
            if (!PlaceOverlap(key, "", x, y, b.Height)) throw new InvalidOperationException($"Could not find matching overlap tile at ({x}, {y})");
            b.Flags |= 0x20;
            UntaggedDoorway(x, y, key);
        }
    }

    /// <summary><c>0x1420f6b70</c> after an untagged shared tile: a 3 × 3 key with a doorway mark across a side
    /// (6 or 0x18) in its footprint is a large door slot; any other is a doorway when its position is marked across
    /// left and right (0x18) and the key is a passage from top to bottom (the same edge at top and bottom, none at the
    /// sides, grounds and heights mirrored left to right), or across top and bottom (6) and the key is one from left
    /// to right.</summary>
    void UntaggedDoorway(int x, int y, TileKey key)
    {
        if (key.Width == 3 && key.Height == 3)
        {
            for (int u = 0; u < 3; u++)
                for (int v = 0; v < 3; v++)
                    if (_doorMarks.TryGetValue((x + u, y + v), out var m) && ((m & 6) == 6 || (m & 0x18) == 0x18))
                    {
                        AddDoorway(x, y, 3, 3);
                        return;
                    }
            return;
        }
        if (!_doorMarks.TryGetValue((x, y), out var mark)) return;
        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var (e, g, h) = (key.Edges, key.Grounds, key.Heights);
        bool vertical = (mark & 0x18) == 0x18 && e[0] is not null && Same(e[0], e[2]) && e[1] is null && e[3] is null &&
            Same(g[0], g[3]) && Same(g[1], g[2]) && h[0] == h[3] && h[1] == h[2];
        bool horizontal = (mark & 6) == 6 && e[1] is not null && Same(e[1], e[3]) && e[2] is null && e[0] is null &&
            Same(g[1], g[0]) && Same(g[2], g[3]) && h[1] == h[0] && h[2] == h[3];
        if (!vertical && !horizontal) return;
        for (int v = 0; v < key.Height; v++)
            for (int u = 0; u < key.Width; u++)
                AddDoorway(x + u, y + v, 1, 1);
    }

    /// <summary><c>0x1420ed9f0</c> (RoomOverlap): each exit of a placed room with a door type marks its doorway on the
    /// room's outer row or column: 3 entries about the exit's middle (unit / 2 into its unit), or 1 when the room or
    /// the unit is under 5 tiles.</summary>
    void MarkDoorways(DungeonPlacement p, int x0, int y0)
    {
        int step = _unit - _overlap, w = p.Width, h = p.Height;
        int tw = step * w + _overlap, th = step * h + _overlap;
        int across = tw < 5 || _unit < 5 ? 1 : 3, down = th < 5 || _unit < 5 ? 1 : 3;
        void Mark(int x, int y, byte side) => _doorMarks[(x, y)] = (byte)(_doorMarks.GetValueOrDefault((x, y)) | side);
        for (int i = 0; i < p.Exits.Length; i++)
        {
            if (p.Exits[i].Type is null) continue;
            if (i < w)
                for (int k = 0; k < across; k++) Mark(x0 + k + step * i - across / 2 + _unit / 2, y0, 4);
            else if (i - w < h)
                for (int k = 0; k < down; k++) Mark(x0 + tw - 1, y0 + k + step * (i - w) - down / 2 + _unit / 2, 0x10);
            else if (i - h - w < w)
                for (int k = 0; k < across; k++) Mark(x0 + k + step * (2 * w - i - 1 + h) - across / 2 + _unit / 2, y0 + th - 1, 2);
            else
                for (int k = 0; k < down; k++) Mark(x0, y0 + k + step * (2 * (h + w) - i - 1) - down / 2 + _unit / 2, 8);
        }
    }

    /// <summary><c>0x1420edd50</c> (no RoomOverlap, a DoorEdgeType): each exit of a placed room with a door type marks
    /// one entry on the room's outer row or column, at the middle of its unit (unit / 2 in), with the side it faces
    /// (0 top, 1 right, 2 bottom, 3 left); a later room's mark replaces an earlier one at the same entry. A set sorted
    /// by x · 0x10000 + y (tiler +0x78).</summary>
    void MarkDoorSides(DungeonPlacement p, int x0, int y0)
    {
        int step = _unit - _overlap, w = p.Width, h = p.Height;
        int tw = step * w + _overlap, th = step * h + _overlap;
        for (int i = 0; i < p.Exits.Length; i++)
        {
            if (p.Exits[i].Type is null) continue;
            if (i < w) _doorSides[((x0 + _unit / 2 + step * i) << 16) + y0] = 0;
            else if (i - w < h) _doorSides[((x0 + tw - 1) << 16) + y0 + _unit / 2 + step * (i - w)] = 1;
            else if (i - w - h < w) _doorSides[((x0 + tw - 1 - step * (i - w - h) - _unit / 2) << 16) + y0 + th - 1] = 2;
            else _doorSides[(x0 << 16) + y0 + th - 1 - step * (i - 2 * w - h) - _unit / 2] = 3;
        }
    }

    readonly SortedDictionary<int, int> _doorSides = [];
    static readonly (int X, int Y)[] SideStep = [(0, -1), (1, 0), (0, 1), (-1, 0)];

    /// <summary><c>0x1420f4d90</c>: each marked entry, in order, whose neighbour on its side is marked facing back: when
    /// both can (<see cref="DoorEdgeTile"/> checking), the door edge goes on both, the marked one first.</summary>
    void DoorEdgeTiles(string door)
    {
        foreach (var (k, side) in _doorSides.ToList())
        {
            var (x, y) = (k >> 16, k & 0xffff);
            var (nx, ny) = (x + SideStep[side].X, y + SideStep[side].Y);
            int back = (side + 2) & 3;
            if (!_doorSides.TryGetValue((nx << 16) + ny, out int other) || other != back) continue;
            if (DoorEdgeTile(door, x, y, back, true) && DoorEdgeTile(door, nx, ny, side, true))
            {
                DoorEdgeTile(door, x, y, back, false);
                DoorEdgeTile(door, nx, ny, side, false);
            }
        }
    }

    /// <summary><c>0x1420f4fd0</c>: the tile at (x, y) (its key in its orientation; an empty entry with a pending fill
    /// record: a 1 × 1 key of that ground and height, points 3, no edges) takes the door edge on the side facing away
    /// from <paramref name="side"/> (point at the entry's middle), unless it has it already. A key without the door
    /// edge yet first tries it on <paramref name="side"/> too, a door through the tile, going on into the tile
    /// beyond (which must take it as well, unless the door edge can end, .et +0x48 flag 0x10). The "door" tiles with
    /// that key: <c>Next(total)</c> picks one when not <paramref name="check"/>ing (<c>0x1420f58a0</c>: the footprint's
    /// vertices lose their ground, then the tile goes in at the entry's height).</summary>
    bool DoorEdgeTile(string door, int x, int y, int side, bool check)
    {
        int bs = (side + 2) & 3;
        if (!_grid.Inside(x, y)) return false;
        TileKey key;
        int ox = x, oy = y;
        short height = 0;
        if (_grid[x, y] is { } e)
        {
            key = e.Tile.Key.Rotate(e.Orientation);
            (ox, oy) = (x - e.OffsetX, y - e.OffsetY);
            height = e.HeightOffset;
        }
        else
        {
            int r = _fillRecords.FindLastIndex(f => f.X == x && f.Y == y);
            if (r < 0) return false;
            var f = _fillRecords[r];
            key = new TileKey { Width = 1, Height = 1 };
            for (int i = 0; i < 4; i++) (key.Grounds[i], key.Heights[i], key.PointA[i], key.PointB[i]) = (f.Ground, (sbyte)f.Height, 3, 3);
        }
        bool Is(string? edge) => edge is not null && string.Equals(edge, door, StringComparison.OrdinalIgnoreCase);
        if (Is(key.Edges[bs])) return true;
        byte Point(int s, TileKey k) => (byte)(s switch
        {
            0 => 3 * (x - ox) + 1,
            1 => 3 * (y - oy) + 1,
            2 => 3 * (ox - x + k.Width) - 2,
            _ => 3 * (oy - y + k.Height) - 2,
        });
        var one = key.Clone();
        one.Edges[bs] = door;
        one.PointA[bs] = Point(bs, one);
        bool Place(List<(TileDef Tile, int Orientation, int Weight)> list)
        {
            if (check) return true;
            var (tile, orientation) = Weighted(list, (int)rng.Next((uint)list.Sum(c => c.Weight)));
            for (int v = 0; v <= key.Height; v++)
                for (int u = 0; u <= key.Width; u++)
                    if (_grid.InsideCorners(ox + u, oy + v))
                    {
                        ref var c = ref _grid.Corner(ox + u, oy + v);
                        c.Flags &= unchecked((byte)~1);
                        c.Ground = null;
                    }
            _grid.Place(tile, orientation, ox, oy, height);
            return true;
        }
        if (!key.Edges.Any(Is))
        {
            var two = one.Clone();
            two.Edges[side] = door;
            two.PointA[side] = Point(side, two);
            var list = _store.Candidates("door", two);
            if (list.Count != 0)
            {
                var (nx, ny) = (x + SideStep[side].X, y + SideStep[side].Y);
                if (side == 0) ny -= key.Height - 1;
                else if (side == 2) ny += key.Height - 1;
                else if (side == 3) nx -= key.Width - 1;
                else nx += key.Width - 1;
                if (DoorEdgeTile(door, nx, ny, side, check) || files.Edge(door)?.CanEnd == true) return Place(list);
            }
        }
        var single = _store.Candidates("door", one);
        return single.Count != 0 && Place(single);
    }

    /// <summary><c>0x1420edf80</c>: a 1 × 1 position joins the doorway of a neighbour (above, else below, else left,
    /// else right) or starts one; anything larger goes to the large door slots.</summary>
    void AddDoorway(int x, int y, int w, int h)
    {
        if (w != 1 || h != 1)
        {
            _bigDoors.Add((x, y));
            return;
        }
        int Of(int u, int v) => _grid.Inside(u, v) ? _doorwayOf[v * _grid.Width + u] : 0;
        int g = Of(x + 1, y);
        if (Of(x - 1, y) is var l and not 0) g = l;
        if (Of(x, y + 1) is var d and not 0) g = d;
        if (Of(x, y - 1) is var t and not 0) g = t;
        if (g == 0)
        {
            _doorways.Add([(x, y)]);
            g = _doorways.Count;
        }
        else _doorways[g - 1].Add((x, y));
        _doorwayOf[y * _grid.Width + x] = g;
    }

    /// <summary><c>0x1420f4010</c>: each doorway without a "door" tile yet gets one. It runs down (its first two
    /// positions share x, or every position is marked across left and right) or across; at its first lowest
    /// position, the tile there gives the key, and the door tiles with that key's edges give the sizes that fit
    /// ("No door found that fits"). <c>Next(count)</c>, then a shuffle; each size in turn, its key with that size
    /// (edge points left at none), until one has door tiles ("No matching door tile found"). A door one entry long
    /// goes to the position at the unit's middle when the unit is odd, else <c>Next(length − size + 1)</c> picks
    /// the offset; <c>Next(total)</c> the tile, placed at the height there.</summary>
    void DoorTiles()
    {
        foreach (var door in _doorways)
        {
            if (door.Any(q => _grid[q.X, q.Y] is { } t && t.Tile.Tag == "door")) continue;
            bool down = door.Count >= 2 && door[0].X == door[1].X ||
                door.All(q => _doorMarks.TryGetValue(q, out var m) && (m & 0x18) == 0x18);
            int first = 0;
            for (int i = 1; i < door.Count; i++)
                if ((down ? door[i].Y : door[i].X) < (down ? door[first].Y : door[first].X)) first = i;
            int width = down ? 1 : door.Count, height = down ? door.Count : 1;
            var (x0, y0) = door[first];
            if (_grid[x0, y0] is not { } at) continue;
            var key = at.Tile.Key.Rotate(at.Orientation);
            var all = _store.Sizes("door", key.Edges);
            if (all.Count == 0) continue;
            var sizes = all.Where(s => s.W <= width && s.H <= height).ToList();
            if (sizes.Count == 0) throw new InvalidOperationException($"No door found that fits {width}x{height} at ({x0}, {y0})");
            rng.Next((uint)sizes.Count);
            rng.Shuffle(sizes);
            List<(TileDef Tile, int Orientation, int Weight)>? list = null;
            int sw = 0, sh = 0;
            foreach (var (w, h) in sizes)
            {
                var k = key.Clone();
                k.Width = (byte)w;
                k.Height = (byte)h;
                for (int i = 0; i < 4; i++)
                {
                    byte length = (byte)(3 * (i % 2 == 0 ? w : h));
                    if (k.PointA[i] == 3) k.PointA[i] = length;
                    k.PointB[i] = length;
                }
                list = _store.Candidates("door", k);
                (sw, sh) = (w, h);
                if (list.Count != 0) break;
            }
            if (list is null || list.Count == 0) throw new InvalidOperationException($"No matching door tile found in tile list at ({x0}, {y0})");
            int size = down ? sh : sw, offset = -1;
            if (size == 1 && (_unit & 1) != 0)
                for (int i = 0; i < door.Count && offset < 0; i++)
                    if ((down ? door[i].Y - _oy : door[i].X - _ox) % (_unit - _overlap) == _unit >> 1) offset = i;
            if (offset < 0) offset = (int)rng.Next((uint)(door.Count - size + 1));
            int px = x0 + (down ? 0 : offset), py = y0 + (down ? offset : 0);
            var (tile, orientation) = Weighted(list, (int)rng.Next((uint)list.Sum(c => c.Weight)));
            _grid.Place(tile, orientation, px, py, _grid[px, py]?.HeightOffset ?? 0);
        }
    }

    /// <summary><c>0x1420f4ae0</c>: each large door slot whose entry starts an untagged tile takes a door tile of that
    /// tile's key (<c>Next(total)</c>), placed over it.</summary>
    void BigDoorTiles()
    {
        foreach (var (x, y) in _bigDoors)
        {
            if (_grid[x, y] is not { IsOrigin: true } e || e.Tile.Tag.Length != 0) continue;
            var list = _store.Candidates("door", e.Tile.Key.Rotate(e.Orientation));
            if (list.Count == 0) continue;
            var (tile, orientation) = Weighted(list, (int)rng.Next((uint)list.Sum(c => c.Weight)));
            _grid.Place(tile, orientation, x, y, e.HeightOffset, force: true);
        }
    }

    /// <summary><c>0x1420eec10</c>: the footprint's vertices without flag 2 take the height (and flag 2); each of its
    /// entries is recorded for the fill with the key's first corner ground, the room's accessible bit and the height.</summary>
    void PendingFill(int x, int y, TileKey key, int height, bool accessible = false)
    {
        for (int v = 0; v <= key.Height; v++)
            for (int u = 0; u <= key.Width; u++)
            {
                if (!_grid.InsideCorners(x + u, y + v)) continue;
                ref var c = ref _grid.Corner(x + u, y + v);
                if ((c.Flags & 2) != 0) continue;
                c.Height = (sbyte)height;
                c.Flags |= 2;
            }
        for (int v = 0; v < key.Height; v++)
            for (int u = 0; u < key.Width; u++)
                if (_grid.Inside(x + u, y + v)) _fillRecords.Add((x + u, y + v, key.Grounds[0], (short)height, accessible));
    }

    readonly List<(int X, int Y, string? Ground, short Height, bool Accessible)> _fillRecords = [];
    (int X0, int Y0, int X1, int Y1) _record;
    // Tiler +0x40 (0x1420f8df0): placed tiles with an edge type of the FillTiles object's +0x140 list.
    readonly List<(int X, int Y, int Index)> _edgeRecords = [];
    List<EdgePoint> _edgePoints = [];
    readonly List<PlacedRoom> _fillRooms = [];
    // Extra feature rooms tilised into mission tiles (0x1420ef310): their objects are placed (1_3_15's Blight
    // encounter spore yellowOrange2.ao); where the client puts them in the room list is not known (they make no draws).
    readonly List<PlacedRoom> _injectedRooms = [];
    // Tiler +0xb0: view cells of wiggling room slots (0x1420ef310) and tiles the listener saw (0x1420f1230), a set
    // sorted by x · 0x10000 + y (0x14204e650).
    readonly SortedDictionary<int, (int X, int Y)> _wiggleCells = [];

    void NoteWiggle(int x, int y) => _wiggleCells.TryAdd(x * 0x10000 + y, (x, y));

    /// <summary><c>0x1420f1230</c>, the listener the dungeon tiler puts on its FillTiles placer (the shrines, edge fill,
    /// overlays and fill; view coordinates): a placed tile that wiggles has its view cell noted. Then, with heights and a
    /// graph top or bottom ground, every vertex of the tile without a ground takes the top ground at or above the
    /// layout's highest level, else the bottom ground at or below its lowest (the levels of <see cref="RoomGrounds"/>,
    /// listener +0x1c / +0x20). MapWorldsCourtyardOfWasting's fill rooms take the graph's deep_snow.gt this way.</summary>
    void Listen(TileDef tile, int o, int x, int y)
    {
        if (Wiggles(tile, 1)) NoteWiggle(x, y);
        var (top, bottom, _) = _graphGrounds;
        if (!_grid.HasHeights || (top is null && bottom is null)) return;
        var (w, h) = Orientation.Size(o, tile.Width, tile.Height);
        for (int v = 0; v <= h; v++)
            for (int u = 0; u <= w; u++)
            {
                int cx = x + u + _view.X0, cy = y + v + _view.Y0;
                if (!_grid.InsideCorners(cx, cy) || _grid.Corner(cx, cy).Ground is not null) continue;
                int at = _grid.Corner(cx, cy).Height;
                if (at >= _levels.High && top is not null) _grid.SetGround(cx, cy, top);
                else if (at <= _levels.Low && bottom is not null) _grid.SetGround(cx, cy, bottom);
            }
    }

    /// <summary><c>0x1420ed900</c>'s test: an untagged (tile data +0x10) square tile whose edges all have its size
    /// (.et +0x4b), at least one of them with a wiggle (+0x4d) of at least <paramref name="least"/>.</summary>
    bool Wiggles(TileDef tile, int least = 1) => tile.Tag.Length == 0 && Wiggles(tile.Key, least);

    bool Wiggles(TileKey key, int least)
    {
        if (key.Width != key.Height) return false;
        bool any = false;
        foreach (var e in key.Edges)
        {
            if (e is null) continue;
            var et = files.Edge(e);
            if (et is null || et.Size != key.Width) return false;
            if (et.Wiggle >= least) any = true;
        }
        return any;
    }

    static readonly int[] SideX = [0, 1, 0, -1], SideY = [-1, 0, 1, 0];

    /// <summary><c>0x140df93f0</c>: the edge point a cell's side takes, from its view position (side 1 and 2 as the
    /// neighbour's side 3 and 0, mirrored).</summary>
    static byte WigglePoint(int x, int y, int side, int size)
    {
        uint u;
        switch (side)
        {
            case 1:
                u = unchecked((uint)(y * 0x7bb + 0x31e9 + (x + size) * 0x337)) % 13;
                return (byte)((2 - u % 3) * size + (size >> 1));
            case 2:
                u = unchecked((uint)((y + size) * 0x7bb + x * 0x337)) % 13;
                return (byte)((2 - u % 3) * size + (size >> 1));
            default:
                u = unchecked((uint)(y * 0x7bb + side * 0x10a3 + x * 0x337)) % 13;
                return (byte)(u % 3 * size + (size >> 1));
        }
    }

    /// <summary><c>0x1420f8f20</c>: the noted cells (<c>Next(count)</c>, shuffled) whose tile still wiggles at its
    /// origin are re-tiled with moved edge points. Each side in a shuffled order (<c>Next(4)</c>, shuffle) whose edge
    /// wiggles takes the point <see cref="WigglePoint"/> gives when that differs and the neighbour across it (one
    /// edge size on) wiggles too: when the TileSet has untagged tiles for the moved key and for the neighbour's key
    /// with the mirrored point (3 · size − p − 1), the move stays. With any move, the cell and then each such neighbour
    /// (sides 0 … 3) get a tile from their lists (<c>Next(total weight)</c>, placed forced at their old height,
    /// <c>0x141ffb870</c>).</summary>
    void Wiggle()
    {
        if (_wiggleCells.Count == 0) return;
        var (vx, vy, vx1, vy1) = _view;
        var cells = _wiggleCells.Values.ToList();
        _wiggleCells.Clear();
        rng.Next((uint)cells.Count);
        rng.Shuffle(cells);
        foreach (var (x, y) in cells)
        {
            if (_grid[x + vx, y + vy] is not { IsOrigin: true } at || !Wiggles(at.Tile)) continue;
            var key = at.Tile.Key.Rotate(at.Orientation);
            int[] sides = [0, 1, 2, 3];
            rng.Next(4u);
            rng.Shuffle(sides);
            List<(TileDef Tile, int Orientation, int Weight)>? mine = null;
            var near = new (int X, int Y, short Height, List<(TileDef Tile, int Orientation, int Weight)>? List)[4];
            foreach (int s in sides)
            {
                if (key.Edges[s] is not { } e || files.Edge(e) is not { Wiggle: not 0 } et) continue;
                int size = et.Size;
                byte p = WigglePoint(x, y, s, size);
                if (p == key.PointA[s]) continue;
                int nx = x + size * SideX[s], ny = y + size * SideY[s];
                near[s] = (nx, ny, 0, null);
                if ((uint)nx >= (uint)(vx1 - vx) || (uint)ny >= (uint)(vy1 - vy)) continue;
                var nb = _grid[nx + vx, ny + vy];
                near[s].Height = nb?.HeightOffset ?? 0;
                if (nb is not { IsOrigin: true } n || !Wiggles(n.Tile)) continue;
                byte old = key.PointA[s];
                key.PointA[s] = p;
                var list = _store.Candidates("", key);
                if (list.Count != 0)
                {
                    var nk = n.Tile.Key.Rotate(n.Orientation);
                    nk.PointA[(s + 2) & 3] = (byte)(3 * size - p - 1);
                    near[s].List = _store.Candidates("", nk);
                    if (near[s].List!.Count != 0)
                    {
                        mine = list;
                        continue;
                    }
                }
                key.PointA[s] = old;
            }
            if (mine is null) continue;
            void Put(List<(TileDef Tile, int Orientation, int Weight)> list, int px, int py, short height)
            {
                int r = (int)rng.Next((uint)list.Sum(c => c.Weight));
                var pick = list[^1];
                foreach (var c in list)
                {
                    if (r < c.Weight) { pick = c; break; }
                    r -= c.Weight;
                }
                _grid.Place(pick.Tile, pick.Orientation, px + vx, py + vy, height, force: true);
            }
            Put(mine, x, y, at.HeightOffset);
            foreach (var nb in near)
                if (nb.List is { Count: > 0 } l) Put(l, nb.X, nb.Y, nb.Height);
        }
    }

    /// <summary><c>0x141feffb0</c> for the outer ground: the first fill tile of its ground, or of the default ones when the
    /// FillTiles do not name it (<c>0x141fefeb0</c>).</summary>
    internal static TileDef OuterTile(TerrainCache files, Dictionary<string, List<(int Weight, string Path, int Orientation)>> fills, GroundType ground)
    {
        if ((!fills.TryGetValue(ground.Name, out var list) && !fills.TryGetValue("default", out list)) || list.Count == 0)
            throw new InvalidOperationException("No fill tile found for outer fill ground type");
        return files.Tile(list[0].Path) ?? throw new FileNotFoundException(list[0].Path);
    }

    // A 3 × 3 tile on a view whose origin and size are all 1 mod 3 goes on a grid of 3 that starts one entry out.
    static int OuterAlign(TileDef tile, (int X0, int Y0, int X1, int Y1) view) =>
        tile.Width == 3 && tile.Height == 3 && view.X0 % 3 == 1 && view.Y0 % 3 == 1 && (view.X1 - view.X0) % 3 == 1 && (view.Y1 - view.Y0) % 3 == 1 ? 1 : 0;

    /// <summary><c>0x14201e4f0</c>'s test: inside the view (one entry more on each side on the grid of 3) on free entries.</summary>
    internal static bool OuterFits(TileGrid grid, TileDef tile, (int X0, int Y0, int X1, int Y1) view, int a, int x, int y)
    {
        int rx = x - view.X0, ry = y - view.Y0, tw = tile.Width, th = tile.Height, vw = view.X1 - view.X0, vh = view.Y1 - view.Y0;
        if (a == 0 ? rx < 0 || ry < 0 || rx + tw > vw || ry + th > vh : rx < -1 || ry < -1 || rx + tw > vw + 1 || ry + th > vh + 1)
            return false;
        for (int v = 0; v < th; v++)
            for (int u = 0; u < tw; u++)
                if (!grid.Inside(x + u, y + v) || grid[x + u, y + v] is not null) return false;
        return true;
    }

    /// <summary><c>0x14201db80</c> up to the height fixing: the view's vertices with flag 1 get flag 2. Without
    /// <paramref name="fixHeights"/> (a tile graph's call, param_3 = 0, or an outer ground with .gt flag 1) every spot on
    /// the tile's grid where it fits takes the tile at orientation 0 (<c>0x14201e4f0</c>: the footprint's vertices take
    /// the plain height, master +0x153 taken as 0, and flag 2), no draw, and null comes back; else the grid's start
    /// (0, or 1 for a grid of 3 one entry out).</summary>
    internal static int? OuterGroundStart(TileGrid grid, TileDef tile, (int X0, int Y0, int X1, int Y1) view, bool fixHeights)
    {
        var (x0, y0, x1, y1) = view;
        int a = OuterAlign(tile, view), tw = tile.Width, th = tile.Height, vw = x1 - x0, vh = y1 - y0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if ((grid.Corner(x, y).Flags & 1) != 0) grid.Corner(x, y).Flags |= 2;
        if (fixHeights) return a;
        for (int y = y0 - a; y < y1 - vh % th; y++)
            for (int x = x0 - a; x < x1 - vw % tw; x++)
                if (OuterFits(grid, tile, view, a, x, y))
                {
                    for (int v = 0; v <= th; v++)
                        for (int u = 0; u <= tw; u++)
                        {
                            ref var c = ref grid.Corner(x + u, y + v);
                            c.Height = 0;
                            c.Flags |= 2;
                        }
                    grid.Place(tile, 0, x, y, 0);
                }
        return null;
    }

    /// <summary><c>0x142044f30</c> → <c>0x14201db80</c>: with the master's OuterGroundType, the vertices with a ground
    /// get flag 2, then the empty entries take that ground's first fill tile (<c>0x141feffb0</c>; "any" orientation
    /// becomes 0), no draw. For a dungeon whose outer ground has no flag 1 (and master +0x161 clear, taken to be so)
    /// the heights are fixed: row by row, an empty entry with a written height at a corner takes the middle of its
    /// corners' heights (<c>0x14201ec30</c> / <c>0x14201e930</c>), the others wait for the next round ("Could not fix
    /// heights in dungeon" when a round settles none); then, up to 15 times, every outer tile (also those the fill
    /// laid) whose corners' middle differs takes it (<c>0x14201ed90</c>, <c>0x141d82d00</c>), until no tile moved by
    /// more than one level. Only 1 × 1 outer tiles are ported (<c>0x14201e4f0</c> also lays 3 × 3 ones on a grid of
    /// 3), the subgraph margins (master +0x170..+0x173) are taken as 0 and the plain height (master +0x153) as 0.</summary>
    void OuterGround(Dictionary<string, string> master, string masterPath)
    {
        if (!master.TryGetValue("OuterGroundType", out var gt)) return;
        // A master without FillTiles has no outer ground tiles (VampireManor's master_container.tsi).
        if (_fills.Count == 0) return;
        var ground = files.Ground(TerrainText.Resolve(masterPath, gt)) ?? throw new FileNotFoundException(gt);
        // The view is the subgraph's rectangle (0x141ffb6c0): the layout inside the outer buffer, which 0x14201ffa0 fills.
        var (x0, y0, x1, y1) = _view;
        var tile = OuterTile(files, _fills, ground);
        if (OuterGroundStart(_grid, tile, _view, fixHeights: _fixHeights.Value && (ground.Flags & 1) == 0) is not { } a) return;
        int tw = tile.Width, th = tile.Height, vw = x1 - x0, vh = y1 - y0;
        bool Fits(int x, int y) => OuterFits(_grid, tile, _view, a, x, y);
        // 0x14201e930: the written heights at the four corners of the tile covering the entry (1 × 1 when empty).
        int? Middle(int x, int y)
        {
            int ox = x, oy = y, w = 1, h = 1;
            if (_grid[x, y] is { } e)
            {
                (ox, oy) = (x - e.OffsetX, y - e.OffsetY);
                (w, h) = Orientation.Size(e.Orientation, e.Tile.Width, e.Tile.Height);
            }
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (var (vx, vy) in new[] { (ox, oy), (ox + w, oy), (ox, oy + h), (ox + w, oy + h) })
                if ((_grid.Corner(vx, vy).Flags & 2) != 0)
                {
                    int v = _grid.HeightAt(vx, vy);
                    lo = Math.Min(lo, v);
                    hi = Math.Max(hi, v);
                }
            return lo == int.MaxValue ? null : hi - (hi - lo) / 2;
        }
        var outer = new List<(int X, int Y)>();
        var ready = new List<(int X, int Y, int H)>();
        var waiting = new List<(int X, int Y)>();
        // 0x14201ec30: only an entry on the tile's grid settles.
        void Settle((int X, int Y) c)
        {
            if ((c.X - x0 + a) % tw != 0 || (c.Y - y0 + a) % th != 0) return;
            if (Middle(c.X, c.Y) is { } m) ready.Add((c.X, c.Y, m));
            else waiting.Add(c);
        }
        // 0x14201e4f0: where the tile fits, the footprint's (w + 1) × (h + 1) vertices take the height (and flag 2),
        // then the tile goes in at it. The entry joins the outer tiles either way.
        void Lay()
        {
            foreach (var (x, y, h) in ready)
            {
                if (Fits(x, y))
                {
                    for (int v = 0; v <= th; v++)
                        for (int u = 0; u <= tw; u++)
                        {
                            ref var c = ref _grid.Corner(x + u, y + v);
                            c.Height = (sbyte)h;
                            c.Flags |= 2;
                        }
                    _grid.Place(tile, 0, x, y, (short)h);
                }
                outer.Add((x, y));
            }
            ready.Clear();
        }
        for (int y = y0 - a; y < y1; y++)
            for (int x = x0 - a; x < x1; x++)
            {
                if (!_grid.Inside(x, y)) continue;
                if (_grid[x, y] is not { } e) Settle((x, y));
                else if (e.Tile == tile) outer.Add((x, y));
            }
        while (waiting.Count != 0)
        {
            Lay();
            var before = waiting;
            waiting = [];
            foreach (var c in before) Settle(c);
            if (waiting.Count == before.Count) throw new InvalidOperationException("Could not fix heights in dungeon");
        }
        Lay();
        for (int round = 0; round < 15; round++)
        {
            bool settled = true;
            var moves = new List<(int X, int Y, int H)>();
            foreach (var (x, y) in outer)
            {
                int m = Middle(x, y) ?? 0, now = _grid[x, y]?.HeightOffset ?? 0;
                if (m == now) continue;
                moves.Add((x, y, m));
                if (Math.Abs(m - now) > 1) settled = false;
            }
            if (moves.Count == 0) break;
            foreach (var (x, y, h) in moves)
                if (_grid[x, y] is { IsOrigin: true }) _grid.SetHeight(x, y, (short)h);
            if (settled) break;
        }
    }

    /// <summary>The graph's grounds (+0xa0 top, +0xa8 bottom, +0xb0 default), the tiler's +0x30 / +0x28 / +0x20.</summary>
    (string? Top, string? Bottom, string? Default) _graphGrounds;
    // The layout's highest and lowest vertex heights (0x141ffc700 / 0x141ffc7d0), as RoomGrounds found them.
    (int High, int Low) _levels = (int.MaxValue, int.MinValue);

    /// <summary>Room data +0xa (<c>0x141ff4e70</c>, which clears it first): a fill room (+9,
    /// <see cref="DungeonRoomSet.IsFill"/>) whose slots' four key grounds are all in the room ground's class (none or .gt
    /// flag 4, or neither). The "special" rooms of 1_3_9 (dockarm_end_special*.arm) do not have it.</summary>
    bool OwnGround(DungeonRoom room)
    {
        if (!DungeonRoomSet.IsFill(room)) return false;
        bool Open(string? g) => g is null || ((files.Ground(g)?.Flags ?? 0) & 4) != 0;
        bool open = Open(room.Def!.Key.Grounds[0]);
        foreach (var slot in room.Def.Slots)
            if (slot is { Kind: not 'n', Key: { } key } && key.Grounds.Any(g => Open(g) != open)) return false;
        return true;
    }

    /// <summary><c>0x1420f0e10</c> per room, after the room vertex grounds: every vertex of the room (with RoomOverlap,
    /// not its left or top column when the room is not at the map's left or top, nor its right or bottom column when
    /// its cell is not the map's last) without a ground takes the graph's default ground; the top ground instead when
    /// its height is the highest of the layout (<c>0x141ffc700</c>), else the bottom ground when the lowest
    /// (<c>0x141ffc7d0</c>), unless the default is impassable_ground.gt. Written without the ground checks
    /// (<c>0x141d85640</c> with map +0x10 clear). A room with room data +0xa (<see cref="OwnGround"/>) gives its own
    /// ground (+0x13a) in place of the default. Not ported: the master's level overrides +0x146 / +0x144.</summary>
    void RoomGrounds(DungeonMap map, int step)
    {
        const string impassable = "Metadata/Terrain/impassable_ground.gt";
        int x1 = _ox + step * map.Width + map.Overlap, y1 = _oy + step * map.Height + map.Overlap;
        int hi = int.MinValue, lo = int.MaxValue;
        for (int y = _oy; y <= y1; y++)
            for (int x = _ox; x <= x1; x++)
            {
                int h = _grid.InsideCorners(x, y) ? _grid.Corner(x, y).Height : 0;
                hi = Math.Max(hi, h);
                lo = Math.Min(lo, h);
            }
        _levels = (hi, lo);
        bool check = _grid.CheckGrounds;
        _grid.CheckGrounds = false;
        var (top, bottom, fallback) = _graphGrounds;
        foreach (var p in map.Rooms)
        {
            if (p.Room?.Def is not { } def) continue;
            var (w, h) = Orientation.Size(p.Orientation, def.Width, def.Height);
            int x0 = 0, y0 = 0, xn = w + 1, yn = h + 1;
            if (map.Overlap != 0)
            {
                x0 = p.X != 0 ? 1 : 0;
                y0 = p.Y != 0 ? 1 : 0;
                xn -= p.X < map.Width - 1 ? 1 : 0;
                yn -= p.Y < map.Height - 1 ? 1 : 0;
            }
            int ox = _ox + step * p.X, oy = _oy + step * p.Y;
            // Room data +0xa with a ground at +0x13a (the key's first corner): the room's own ground in place of the default.
            string? own = OwnGround(p.Room) && def.Key.Grounds[0] is { } rg ? rg : fallback;
            for (int v = y0; v < yn; v++)
                for (int u = x0; u < xn; u++)
                {
                    int x = ox + u, y = oy + v;
                    if (!_grid.InsideCorners(x, y)) continue;
                    ref var c = ref _grid.Corner(x, y);
                    if ((c.Flags & 1) != 0 && c.Ground is not null) continue;
                    string? g = own;
                    if (!string.Equals(g, impassable, StringComparison.OrdinalIgnoreCase))
                    {
                        if (top is not null && c.Height == hi) g = top;
                        else if (bottom is not null && c.Height == lo) g = bottom;
                    }
                    _grid.SetGround(x, y, g);
                }
        }
        _grid.CheckGrounds = check;
    }

    /// <summary><c>0x1420f3480</c>'s fill: after the shrines (<c>0x1420f2340</c>), the records sorted by (x, y)
    /// (<c>0x14212d520</c>); each still-empty one is filled (<c>0x1420f3de0</c> → <c>0x141feb970</c>) for its ground, or,
    /// without one, its top-left vertex's ground when that vertex has flag 1. The fill's view is the tiler's, so a tile
    /// reaching more than one entry into the outer buffer does not fit. All of it only with FillTiles (master +0x218).
    /// Between the shrines and the fill the recorded edge tiles get their open sides filled (<c>0x141feed20</c>,
    /// <see cref="GroundStages.EdgePoints"/>); then the first overlay pass (<c>0x141ffe240</c> on the tiler's view with the
    /// layout's rooms and the shrines, <c>0x141fff1a0</c> with bit 1), and after the fill the second (bit 2). Returns the
    /// overlays' rooms. Before the shrines the pool's unique rooms (<c>0x1420f1590</c>); not ported after them:
    /// <c>0x1420f3100</c> (pre-unique rooms).</summary>
    List<PlacedRoom> FillRecords(Dictionary<string, string> master, string masterPath, List<PlacedRoom> layoutRooms, bool primary)
    {
        if (!master.TryGetValue("FillTiles", out var gft)) return [];
        var (vx, vy, vx1, vy1) = _view;
        var fill = new Fill(files, rng, _grid, vx, vy, vx1 - vx, vy1 - vy, _store,
            files.FillTiles(TerrainText.Resolve(masterPath, gft)), _fillRooms, notes) { TilePlaced = Listen, PoolHasTiles = () => pool.HasTiles };
        UniqueRooms(master, masterPath, fill, primary);
        Shrines(master, masterPath, fill);
        if (_edgeRecords.Count != 0)
            new GroundStages(_grid, vx, vy, vx1 - vx, vy1 - vy, files).EdgePoints(
                [.. _edgeRecords.Select(r => (r.X - vx, r.Y - vy, r.Index))], _edgePoints, fill.FillSide);
        // Map +0x10 (ground checks) is off from the overlays to the second overlay pass.
        bool check = _grid.CheckGrounds;
        _grid.CheckGrounds = false;
        var passes = master.TryGetValue("Overlays", out var toy) ? Overlays.Parse(files, TerrainText.Resolve(masterPath, toy)) : [];
        var tiler = new RoomTiler(rng, _grid, _record.X0, _record.Y0, _store) { TilePlaced = (tile, o, x, y) => Listen(tile, o, x + _record.X0 - vx, y + _record.Y0 - vy) };
        // The overlays' view is the whole record, its margins included (0x1420f3480 +8 → 0x141ffe240; ExpeditionCemetery's
        // GraveWater overlay reaches into the outer buffer's margin).
        var overlays = passes.Count == 0 ? null : new Overlays(files, rng, notes, _grid, _record.X0, _record.Y0, _record.X1 - _record.X0, _record.Y1 - _record.Y0, tiler,
            [.. layoutRooms, .. _fillRooms], int.Parse(master.GetValueOrDefault("RoomOverlap", "1")), pool.Placed, dungeon: true);
        // Both dungeon passes save the tiles under a room (0x141fff1a0 param 3).
        if (overlays is not null) overlays.FillEntry = (x, y, g, open) => fill.FillEntry(x + _record.X0 - vx, y + _record.Y0 - vy, g, open);
        foreach (var pass in passes.Where(p => (p.Passes & 1) != 0)) overlays!.Run(pass, saveTiles: true);
        foreach (var (x, y, ground, height, accessible) in _fillRecords.OrderBy(r => r.X).ThenBy(r => r.Y))
        {
            if (_grid[x, y] is not null) continue;
            var g = ground ?? ((_grid.Corner(x, y).Flags & 1) != 0 ? _grid.Corner(x, y).Ground : null);
            fill.FillEntry(x - vx, y - vy, g, accessible, height);
        }
        foreach (var pass in passes.Where(p => (p.Passes & 2) != 0)) overlays!.Run(pass, saveTiles: true);
        _grid.CheckGrounds = check;
        return tiler.Rooms;
    }

    /// <summary><c>0x1420f1590</c>: the extra room pool's rows the layout left (<c>DungeonLayout.Inject</c>). With
    /// "mission" rooms in the RoomSet (+0x130) and master +0x15a clear (taken to be so), only the oversize rows (+0x61),
    /// and nothing at all without one. For the first subgraph (+0x92 = 0; a "teleport" record's +0x91 bit 1 is not
    /// tracked): the fill records shuffled in place (<c>Next(count)</c>, the shuffle), then <c>0x1420f17d0</c> twice:
    /// the second time without the distance test, and only while rows are left.</summary>
    void UniqueRooms(Dictionary<string, string> master, string masterPath, Fill fill, bool primary)
    {
        if (pool.Rows.Count == 0) return;
        bool missions = RoomSetOf(master, masterPath).All.Any(r => r.Tag == "mission");
        if (missions && !pool.Rows.Any(r => r.Oversize)) return;
        if (!primary) return;
        rng.Next((uint)_fillRecords.Count);
        rng.Shuffle(_fillRecords);
        var placed = new List<(int X0, int Y0, int X1, int Y1)>();
        UniqueRound(fill, missions, false, placed);
        if (pool.Rows.Count != 0) UniqueRound(fill, missions, true, placed);
    }

    /// <summary><c>0x1420f17d0</c>: a copy of the rows, only the oversize ones when <paramref name="oversizeOnly"/>, else
    /// with the oversize ones moved to the front (an unstable partition); in order, a row that is not oversize goes only
    /// while the share (+0x144) lasts. Its only room, or its rooms shuffled (<c>Next(count)</c>, the shuffle) until one
    /// goes in (<see cref="PlaceUniqueRoom"/>).</summary>
    void UniqueRound(Fill fill, bool oversizeOnly, bool relaxed, List<(int X0, int Y0, int X1, int Y1)> placed)
    {
        var rows = oversizeOnly ? pool.Rows.Where(r => r.Oversize).ToList() : pool.Rows.ToList();
        if (!oversizeOnly)
            for (int i = 0, end = rows.Count; i != end; i++)
            {
                if (rows[i].Oversize) continue;
                do { if (--end == i) goto partitioned; } while (!rows[end].Oversize);
                (rows[i], rows[end]) = (rows[end], rows[i]);
            }
        partitioned:
        foreach (var row in rows)
        {
            if (!row.Oversize && pool.Share == 0) break;
            bool any = row.Orientations.Count == 0 || row.Orientations.Contains(8);
            if (row.Rooms.Count == 1)
            {
                PlaceUniqueRoom(fill, row, row.Rooms[0], any, relaxed, placed);
                continue;
            }
            var rooms = row.Rooms.ToList();
            rng.Next((uint)rooms.Count);
            rng.Shuffle(rooms);
            foreach (var path in rooms)
                if (PlaceUniqueRoom(fill, row, path, any, relaxed, placed)) break;
        }
    }

    /// <summary><c>0x1420f1c10</c>: a room whose key has no edges and one ground on its corners turns by
    /// <c>Next(8)</c> (the row allows any) or one of the row's orientations (<c>Next(count)</c>), and goes on the first
    /// fill record that is accessible, whose ground and top-left vertex ground are none or flags 4 and 8, where it fits
    /// on empty entries inside the view and (first round) is more than 16 (squared rectangle distance,
    /// <c>0x1420f14b0</c>) from the rooms placed here. The vertices it covers with no ground or a flag-8 one take the
    /// record's ground when that is none or flag 8; tilised at the record's height with its ground (open); the row
    /// leaves the pool (counted).</summary>
    bool PlaceUniqueRoom(Fill fill, ExtraFeatureRow row, string path, bool any, bool relaxed, List<(int X0, int Y0, int X1, int Y1)> placed)
    {
        var room = files.Room(path) ?? throw new FileNotFoundException(path);
        if (room.Key.Edges.Any(e => e is not null) || room.Key.Grounds.Distinct().Count() != 1) return false;
        int o = any ? (int)rng.Next(8u) : row.Orientations[(int)rng.Next((uint)row.Orientations.Count)];
        var (w, h) = Orientation.Size(o, room.Width, room.Height);
        int Flags(string? g) => g is null ? 0 : files.Ground(g)?.Flags ?? 0;
        bool Open(string? g) => g is null || (Flags(g) & 0xc) == 0xc;
        bool Loose(string? g) => g is null || (Flags(g) & 8) != 0;
        var (vx, vy, vx1, vy1) = _view;
        static int Gap(int a0, int a1, int b0, int b1) => b1 <= a0 ? a0 - b1 : a1 <= b0 ? b0 - a1 : 0;
        foreach (var (x, y, ground, height, accessible) in _fillRecords)
        {
            if (!accessible || !Open(ground) || !Open(_grid.Corner(x, y).Ground)) continue;
            if (x >= vx1 || x + w > vx1 || y >= vy1 || y + h > vy1) continue;
            bool free = true;
            for (int v = 0; v < h && free; v++)
                for (int u = 0; u < w && free; u++)
                    if (_grid[x + u, y + v] is not null) free = false;
            if (!free) continue;
            if (!relaxed && placed.Any(p => { int dx = Gap(p.X0, p.X1, x, x + w), dy = Gap(p.Y0, p.Y1, y, y + h); return dx * dx + dy * dy <= 16; }))
                continue;
            for (int v = 0; v <= h; v++)
                for (int u = 0; u <= w; u++)
                    if (Loose(_grid.Corner(x + u, y + v).Ground) && Loose(ground)) _grid.SetGround(x + u, y + v, ground);
            notes.Add($"unique room {Path.GetFileName(room.Path)} ({row.Name}) o{o} at ({x}, {y})");
            fill.PlaceRoom(room, x - vx, y - vy, o, height, ground, true);
            pool.RoomPlaced(x, y, w, h);
            pool.Remove(row, true);
            placed.Add((x, y, x + w, y + h));
            return true;
        }
        return false;
    }

    /// <summary><c>0x1420f2340</c>: with shrines rolled (pool +0x148), the RoomSet's "shrine" rooms (+0x118) whose unit
    /// size is not the layout's (the others are layout rooms, <c>DungeonLayout.ShrineRooms</c>): the fill records are
    /// shuffled in place (<c>Next(count)</c>, then the shuffle), then <c>0x1420f2620</c> runs once keeping 17 entries
    /// (squared rectangle distance, <c>0x1420f14b0</c>) from the shrines placed here, and again without that while
    /// shrines are left.</summary>
    void Shrines(Dictionary<string, string> master, string masterPath, Fill fill)
    {
        int unit = int.Parse(master.GetValueOrDefault("RoomUnitSize", "1"));
        var rooms = RoomSetOf(master, masterPath).All
            .Where(r => r.Tag == "shrine" && r.Unit != unit && r.Def is not null).Select(r => r.Def!).ToList();
        if (rooms.Count == 0 || pool.Shrines == 0) return;
        rng.Next((uint)_fillRecords.Count);
        rng.Shuffle(_fillRecords);
        var placed = new List<(int X0, int Y0, int X1, int Y1)>();
        ShrineRound(rooms, placed, fill, false);
        if (pool.Shrines != 0) ShrineRound(rooms, placed, fill, true);
    }

    /// <summary><c>0x1420f2620</c>: the records in order while shrines are left. One that is accessible, whose ground
    /// and top-left vertex ground are none or flags 4 and 8, offers every shrine room that fits there on empty entries
    /// with its corner grounds suiting the vertices (<c>0x141ffba10</c> / <c>0x141d85570</c>), and in the first round not
    /// within 17 of a placed shrine: a square one any way (0xff), another as it is (0x55) and turned (0xaa, its key
    /// turned once). <c>Next(count)</c>, the orientation <c>Next(840)</c> from its mask; tilised at the record's height
    /// with its ground (open), a shrine placed (<c>0x141d8b9a0</c>).</summary>
    void ShrineRound(List<RoomDef> rooms, List<(int X0, int Y0, int X1, int Y1)> placed, Fill fill, bool relaxed)
    {
        int Flags(string? g) => g is null ? 0 : files.Ground(g)?.Flags ?? 0;
        bool Open(string? g) => g is null || (Flags(g) & 0xc) == 0xc;
        bool Suits(int x, int y, string? g)
        {
            ref var c = ref _grid.Corner(x, y);
            if ((c.Flags & 1) == 0 || g == c.Ground) return true;
            return (g is null || (Flags(g) & 8) != 0) && (c.Ground is null || (Flags(c.Ground) & 8) != 0);
        }
        bool Corners(int x, int y, TileKey k) => Suits(x, y, k.Grounds[0]) && Suits(x + k.Width, y, k.Grounds[1]) &&
            Suits(x + k.Width, y + k.Height, k.Grounds[2]) && Suits(x, y + k.Height, k.Grounds[3]);
        bool Free(int x, int y, int w, int h)
        {
            if (x >= _grid.Width || x + w > _grid.Width || y >= _grid.Height || y + h > _grid.Height) return false;
            for (int v = 0; v < h; v++)
                for (int u = 0; u < w; u++)
                    if (_grid[x + u, y + v] is not null) return false;
            return true;
        }
        static int Gap(int a0, int a1, int b0, int b1) => b1 <= a0 ? a0 - b1 : a1 <= b0 ? b0 - a1 : 0;
        bool Near(int x, int y, int w, int h) => !relaxed &&
            placed.Any(p => { int dx = Gap(p.X0, p.X1, x, x + w), dy = Gap(p.Y0, p.Y1, y, y + h); return dx * dx + dy * dy < 17; });
        for (int i = 0; i < _fillRecords.Count && pool.Shrines != 0; i++)
        {
            var (x, y, ground, height, accessible) = _fillRecords[i];
            if (!accessible || !Open(ground) || !Open(_grid.Corner(x, y).Ground)) continue;
            var fitting = new List<(RoomDef Room, byte Mask)>();
            foreach (var room in rooms)
            {
                int w = room.Width, h = room.Height;
                if (w == h)
                {
                    if (Free(x, y, w, h) && Corners(x, y, room.Key) && !Near(x, y, w, h)) fitting.Add((room, 0xff));
                    continue;
                }
                byte mask = 0;
                if (Free(x, y, w, h) && Corners(x, y, room.Key) && !Near(x, y, w, h)) mask = 0x55;
                var turned = room.Key.Rotate(1);
                if (Free(x, y, turned.Width, turned.Height) && Corners(x, y, turned) && !Near(x, y, turned.Width, turned.Height)) mask |= 0xaa;
                if (mask != 0) fitting.Add((room, mask));
            }
            if (fitting.Count == 0) continue;
            var pick = fitting[(int)rng.Next((uint)fitting.Count)];
            int o = LevelRandom.OrientationFromMask(pick.Mask, rng.Next(840u));
            var (pw, ph) = Orientation.Size(o, pick.Room.Width, pick.Room.Height);
            placed.Add((x, y, x + pw, y + ph));
            pool.ShrinePlaced(x, y, pw, ph);
            notes.Add($"shrine {Path.GetFileName(pick.Room.Path)} o{o} at ({x}, {y})");
            fill.PlaceRoom(pick.Room, x - _view.X0, y - _view.Y0, o, height, ground, true);
        }
    }

    /// <summary><c>0x1420ee9e0</c>: no edges, every corner the first corner's ground (grounds left out of keys count as
    /// none), and a height only on a corner whose ground has no flag 1.</summary>
    bool Plain(TileKey key)
    {
        string? G(int i) => key.Grounds[i] is { } g && files.Ground(g) is { IgnoredInKeys: true } ? null : key.Grounds[i];
        for (int i = 0; i < 4; i++)
        {
            if (key.Heights[i] != 0 && (key.Grounds[i] is null || ((files.Ground(key.Grounds[i])?.Flags ?? 0) & 1) != 0)) return false;
            if (key.Edges[i] is not null) return false;
            if (i != 0 && !string.Equals(G(i), G(0), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary><c>0x1420f5fb0</c>: the tiles of the key and tag, <c>Next(total weight)</c>, placed at the slot's height
    /// and then set to it (<c>0x141d82d00</c>, vertices included). Without a direct match, in order until one is found:
    /// the movable sides' points go to the middle one side at a time (<c>0x1420f69c0</c>; a side is movable when its
    /// edge is not virtual (.et flag 4) nor N2 (flag 8), its point is not the middle (3 × length / 2) and the slot
    /// beyond it, at y − size, x + w, y + h or x − size (.et +0x4b), is a shared border slot (master +0x162 taken as
    /// clear)); then, when the key has a height, from the key again: one corner whose ground lacks flag 1 at a time at
    /// height 0 (<c>0x1420f6ab0</c>), the points again, both again; all those corners at 0, the points again; and when
    /// the other corners have one height, all at 0 with that height added to the tile's. A tile placed with moved
    /// points (or for a neighbour) is forced; then the neighbours across the moved sides take the middle point too
    /// (<c>0x1420f5b20</c>). <paramref name="adjacent"/> is that call's flag: no point moves, no neighbours.
    /// Then the edge-type records (<c>0x1420f8df0</c>). Not ported: the descriptions' objects (<c>0x141d83f10</c>).</summary>
    bool PlaceOverlap(TileKey key, string tag, int x, int y, int height, bool adjacent = false)
    {
        var work = key.Clone();
        var moved = new List<int>();
        var found = _store.Candidates(tag, work);
        if (found.Count == 0)
        {
            byte[] middle = [(byte)(key.Width * 3 >> 1), (byte)(key.Height * 3 >> 1)];
            var movable = new List<int>();
            for (int i = 0; i < 4; i++)
            {
                if (files.Edge(key.Edges[i]) is not { } et || et.Virtual || et.N2 || key.PointA[i] == middle[i & 1]) continue;
                var (nx, ny) = Beyond(key, i, x, y, et.Size);
                if ((nx, ny) == (x, y) || !_borders.ContainsKey((nx, ny))) continue;
                movable.Add(i);
            }
            bool canMove = movable.Count != 0 && !adjacent;
            bool Flag1(string? g) => g is null || ((files.Ground(g)?.Flags ?? 0) & 1) != 0;
            bool MovePoints()
            {
                foreach (int i in movable)
                {
                    work.PointA[i] = middle[i & 1];
                    moved.Add(i);
                    if ((found = _store.Candidates(tag, work)).Count != 0) return true;
                }
                return false;
            }
            bool FlattenOne()
            {
                for (int c = 0; c < 4; c++)
                {
                    if (Flag1(work.Grounds[c])) continue;
                    var old = work.Heights[c];
                    work.Heights[c] = 0;
                    if ((found = _store.Candidates(tag, work)).Count != 0) return true;
                    work.Heights[c] = old;
                }
                return false;
            }
            if (canMove) MovePoints();
            if (found.Count == 0 && key.Heights.Any(h => h != 0))
            {
                work = key.Clone();
                moved.Clear();
                bool ok = FlattenOne();
                if (!ok && canMove)
                {
                    ok = MovePoints() || FlattenOne();
                    if (!ok)
                    {
                        work = key.Clone();
                        moved.Clear();
                    }
                }
                if (!ok)
                {
                    var heights = new SortedSet<sbyte>();
                    for (int c = 0; c < 4; c++)
                        if (Flag1(work.Grounds[c])) heights.Add(work.Heights[c]);
                        else work.Heights[c] = 0;
                    found = _store.Candidates(tag, work);
                    if (found.Count == 0 && canMove) MovePoints();
                    if (found.Count == 0 && heights.Count == 1)
                    {
                        Array.Clear(work.Heights);
                        height += heights.Min;
                        found = _store.Candidates(tag, work);
                    }
                }
            }
            if (found.Count == 0) return false;
        }
        var (tile, orientation) = Weighted(found, (int)rng.Next((uint)found.Sum(c => c.Weight)));
        _grid.Place(tile, orientation, x, y, (short)height, force: moved.Count != 0 || adjacent);
        _grid.SetHeight(x, y, (short)height);
        if (!adjacent)
        {
            Adjacent(work, x, y, moved);
            RecordEdgeTypes(key, x, y);
        }
        return true;
    }

    /// <summary>The slot beyond side i of a key at (x, y) (<c>0x1420f5fb0</c>, <c>0x1420f5b20</c>): above and to the
    /// left by the edge's size, to the right and below by the key's.</summary>
    static (int X, int Y) Beyond(TileKey key, int side, int x, int y, int size) => side switch
    {
        0 => (x, y - size),
        1 => (x + key.Width, y),
        2 => (x, y + key.Height),
        _ => (x - size, y),
    };

    /// <summary><c>0x1420f5b20</c>: for each side whose point moved, the slot beyond it: a shared border slot not
    /// placed untagged (flag 0x20) takes the middle point on its facing side when that side has the same edge and
    /// length; else a placed tile there whose turned key has them and another point is placed again with the middle
    /// point (grounds left out of keys dropped, its tag, its height, forced, <see cref="PlaceOverlap"/> with the flag),
    /// or "Could not find matching adjacent tile".</summary>
    void Adjacent(TileKey key, int x, int y, List<int> moved)
    {
        foreach (int i in moved)
        {
            if (key.Edges[i] is not { } edge) continue;
            var (nx, ny) = Beyond(key, i, x, y, files.Edge(edge)?.Size ?? 0);
            if (!_grid.Inside(nx, ny)) continue;
            int facing = (i + 2) & 3, length = (i & 1) == 0 ? key.Width : key.Height;
            byte middle = (byte)(length * 3 >> 1);
            bool Same(TileKey k) => ((facing & 1) == 0 ? k.Width : k.Height) == length &&
                string.Equals(k.Edges[facing], edge, StringComparison.OrdinalIgnoreCase);
            if (_borders.TryGetValue((nx, ny), out var b) && (b.Flags & 0x20) == 0)
            {
                if (Same(b.Key)) b.Key.PointA[facing] = middle;
                continue;
            }
            if (_grid[nx, ny] is not { } e) continue;
            var turned = e.Tile.Key.Rotate(e.Orientation);
            if (!Same(turned) || turned.PointA[facing] == middle) continue;
            for (int c = 0; c < 4; c++)
                if (files.Ground(turned.Grounds[c]) is { IgnoredInKeys: true }) turned.Grounds[c] = null;
            turned.PointA[facing] = middle;
            if (!PlaceOverlap(turned, e.Tile.Tag, nx, ny, e.HeightOffset, adjacent: true))
                throw new InvalidOperationException($"Could not find matching adjacent tile at ({nx}, {ny})");
        }
    }

    /// <summary><c>0x1420ee240</c>: the stored key with the new slot's parts where the new room covers them. A corner
    /// both of whose sides it covers (<c>0x1420ee460</c>; masks top left 8|4, top right 0x10|4, bottom right 0x10|2,
    /// bottom left 8|2) takes its ground (none for a ground left out of keys) and its height, relative to the stored
    /// slot's. A side it covers (<c>0x1420ee900</c>; top 4, right 0x10, bottom 2, left 8) takes its edge and points, or,
    /// when the stored slot covers it too, the two edges combined (<c>0x1420ee580</c>) and the side is marked shared.
    /// Points stay in order.</summary>
    TileKey Merge(BorderSlot b, TileKey key, int height, byte flags, int x, int y)
    {
        var old = b.Key;
        if (key.Width != old.Width || key.Height != old.Height)
            throw new InvalidOperationException($"Cannot combine tile keys of different sizes at ({x}, {y})");
        var k = old.Clone();
        ReadOnlySpan<byte> corners = [0xc, 0x14, 0x12, 0xa], sides = [4, 0x10, 2, 8];
        for (int i = 0; i < 4; i++)
        {
            if ((flags & corners[i]) != corners[i]) continue;
            var g = key.Grounds[i];
            k.Grounds[i] = g is null || files.Ground(g) is { IgnoredInKeys: true } ? null : g;
            k.Heights[i] = (sbyte)(key.Heights[i] - b.Height + height);
        }
        for (int i = 0; i < 4; i++)
        {
            if ((flags & sides[i]) == 0) continue;
            if ((b.Flags & sides[i]) == 0)
            {
                k.PointA[i] = key.PointA[i];
                k.PointB[i] = key.PointB[i];
                k.Edges[i] = key.Edges[i];
            }
            else
            {
                CombineSide(old, key, k, i);
                b.Sides |= sides[i];
            }
            if (k.PointB[i] < k.PointA[i]) (k.PointA[i], k.PointB[i]) = (k.PointB[i], k.PointA[i]);
        }
        return k;
    }

    /// <summary><c>0x1420ee580</c>: two edges on one side become the master's EdgeCombination's (.ecf) combination for
    /// the height step along the side (<c>0x1420f9880</c>): one edge alone stays, two without a combination give none.
    /// No edge: points at the side's length (3 per tile); one: its points; two: each point where both agree, else the
    /// middle.</summary>
    void CombineSide(TileKey old, TileKey key, TileKey k, int i)
    {
        string? a = old.Edges[i], b = key.Edges[i];
        if (a is null && b is null) return;
        string? r;
        if (_combiner is null)
        {
            if (a is not null && b is not null) throw new InvalidOperationException("No EdgeTypeCombiner (.ecf) specified");
            r = a ?? b;
        }
        else r = _combiner.Combine(a, b, Math.Abs(k.Heights[i] - k.Heights[(i + 1) & 3]));
        byte length = (byte)(((i & 1) == 0 ? k.Width : k.Height) * 3);
        k.Edges[i] = r;
        if (r is null) k.PointA[i] = k.PointB[i] = length;
        else if (a is null) (k.PointA[i], k.PointB[i]) = (key.PointA[i], key.PointB[i]);
        else if (b is null) (k.PointA[i], k.PointB[i]) = (old.PointA[i], old.PointB[i]);
        else
        {
            k.PointA[i] = key.PointA[i] == old.PointA[i] ? key.PointA[i] : (byte)(length >> 1);
            k.PointB[i] = key.PointB[i] == old.PointB[i] ? key.PointB[i] : (byte)(length >> 1);
        }
    }

    /// <summary>The grid position of a room slot's tile: the turned footprint's top-left corner.</summary>
    static (int X, int Y) SlotOrigin(int code, int col, int row, int w, int h, int roomW, int roomH)
    {
        int minX = int.MaxValue, minY = int.MaxValue;
        foreach (var (cx, cy) in new[] { (col, row), (col + w - 1, row), (col, row + h - 1), (col + w - 1, row + h - 1) })
        {
            var (gx, gy) = Orientation.Map(code, cx, cy, roomW, roomH);
            minX = Math.Min(minX, gx);
            minY = Math.Min(minY, gy);
        }
        return (minX, minY);
    }

    static (TileDef Tile, int Orientation) Weighted(List<(TileDef Tile, int Orientation, int Weight)> list, int r)
    {
        foreach (var c in list)
        {
            if (r < c.Weight) return (c.Tile, c.Orientation);
            r -= c.Weight;
        }
        return (list[^1].Tile, list[^1].Orientation);
    }

}

/// <summary>The tiles of a master's TileSet, indexed the way <c>0x141d81590</c> files them: for each tile in list
/// order and each orientation 0..7, under (tag, key turned by the orientation).</summary>
sealed class TileStore
{
    readonly Dictionary<(string Tag, TileKey Key), List<(TileDef Tile, int Orientation, int Weight)>> _byKey = [];
    readonly HashSet<(string Tag, string? Top, string? Right, string? Bottom, string? Left)> _edges = [];
    readonly Dictionary<(string Tag, string? Top, string? Right, string? Bottom, string? Left), List<(int W, int H)>> _sizes = [];
    readonly Dictionary<(string? Top, string? Right, string? Bottom, string? Left, int W, int H), List<(TileDef Tile, int Orientation, int Weight)>> _bySize = [];

    readonly TerrainCache _files;

    /// <summary>The TileSet's post-unique tiles (tag "postunique", +0x80): not filed, placed by <c>0x14203ece0</c>
    /// with their orientation mask.</summary>
    public readonly List<(TileDef Tile, int Mask)> PostUnique = [];

    public TileStore(TerrainCache files, IEnumerable<(int Weight, string Path, int Mask)> tiles)
    {
        _files = files;
        foreach (var (weight, path, lineMask) in tiles)
        {
            if (files.Tile(path) is not { } tile) continue;
            // 0x141d81590: the line's orientations, kept to the tile's own restriction mask (tile data +0x171).
            int mask = tile.AllowedOrientations(lineMask);
            if (tile.Tag == "postunique")
            {
                PostUnique.Add((tile, mask));
                continue;
            }
            for (int o = 0; o < 8; o++)
            {
                if ((mask & 1 << o) == 0) continue;
                var key = (tile.Tag, tile.Key.Rotate(o));
                if (!_byKey.TryGetValue(key, out var list)) _byKey[key] = list = [];
                list.Add((tile, o, weight));
                var k = key.Item2.Edges;
                _edges.Add((tile.Tag, k[0], k[1], k[2], k[3]));
                var sizes = _sizes.TryGetValue((tile.Tag, k[0], k[1], k[2], k[3]), out var sl) ? sl : _sizes[(tile.Tag, k[0], k[1], k[2], k[3])] = [];
                var size = ((int)key.Item2.Width, (int)key.Item2.Height);
                // 0x141d8c620: a set kept sorted by (w, h).
                if (!sizes.Contains(size))
                {
                    int at = sizes.FindIndex(z => z.Item1 > size.Item1 || (z.Item1 == size.Item1 && z.Item2 > size.Item2));
                    sizes.Insert(at < 0 ? sizes.Count : at, size);
                }
                var bySize = (k[0], k[1], k[2], k[3], size.Item1, size.Item2);
                if (!_bySize.TryGetValue(bySize, out var bl)) _bySize[bySize] = bl = [];
                bl.Add((tile, o, weight));
            }
        }
    }

    /// <summary><c>0x141d81f70</c>: the tiles (with their orientation) whose key is this one, its grounds left out of
    /// keys dropped first (<c>0x141d813c0</c>). With none, the corners whose ground has no flag 1 lose their height
    /// and the lookup repeats.</summary>
    public List<(TileDef Tile, int Orientation, int Weight)> Candidates(string tag, TileKey key)
    {
        var k = key.Clone();
        for (int c = 0; c < 4; c++)
            if (_files.Ground(k.Grounds[c]) is { IgnoredInKeys: true }) k.Grounds[c] = null;
        if (_byKey.TryGetValue((tag, k), out var list) && list.Count != 0) return list;
        bool changed = false;
        for (int c = 0; c < 4; c++)
            if (k.Heights[c] != 0 && k.Grounds[c] is { } g && ((_files.Ground(g)?.Flags ?? 0) & 1) == 0)
            {
                k.Heights[c] = 0;
                changed = true;
            }
        return changed ? Candidates(tag, k) : [];
    }

    /// <summary>Some tile has the tag (the tag map holds it).</summary>
    public bool HasTag(string tag) => _byKey.Keys.Any(k => k.Tag == tag);

    /// <summary><c>0x141d811c0</c> found sizes: some tile of the tag has these edges (top, right, bottom, left) in some
    /// orientation.</summary>
    public bool HasEdges(string tag, string?[] edges) => _edges.Contains((tag, edges[0], edges[1], edges[2], edges[3]));

    /// <summary><c>0x141d811c0</c>: the sizes of the tag's tiles (in any orientation) with these edges, sorted by (w, h).</summary>
    public IReadOnlyList<(int W, int H)> Sizes(string tag, string?[] edges) =>
        _sizes.TryGetValue((tag, edges[0], edges[1], edges[2], edges[3]), out var l) ? l : [];

    /// <summary><c>0x141d82280</c>: every tile of any tag, in each orientation, with these edges and this size.</summary>
    public List<(TileDef Tile, int Orientation, int Weight)>? ByEdgesAndSize(string?[] edges, int w, int h) =>
        _bySize.TryGetValue((edges[0], edges[1], edges[2], edges[3], w, h), out var l) ? l : null;
}

/// <summary>A master's EdgeCombination (.ecf, <c>0x1420f9880</c>'s map at master +0x208): what two edge types on one
/// side of a shared border tile become. Read as "version", then lines of three quoted paths (a, b, result; "" = none);
/// the client keys its map by the height step and a flag too, which this reader does not know yet.</summary>
sealed class EdgeCombiner
{
    /// <summary>By the two edges ("" for none) and the height step, null for a line without one.</summary>
    readonly Dictionary<(string, string, int?), string?> _pairs = [];

    /// <summary>Lines of two edges, the combination and an optional height step.</summary>
    public static EdgeCombiner Parse(string text, string relativeTo)
    {
        var c = new EdgeCombiner();
        foreach (var line in TerrainText.Lines(text))
        {
            var all = TerrainText.Tokenize(line);
            var t = all.Where(x => x.Quoted).ToList();
            if (t.Count < 3) continue;
            string Key(string text) => text.Length == 0 ? "" : TerrainText.Resolve(relativeTo, text).ToLowerInvariant();
            string? r = t[2].Text.Length == 0 ? null : TerrainText.Resolve(relativeTo, t[2].Text);
            int? step = all.LastOrDefault() is { Quoted: false, IsNumber: true } n ? n.Int : null;
            c._pairs[(Key(t[0].Text), Key(t[1].Text), step)] = r;
        }
        return c;
    }

    /// <summary><c>0x1420f9880</c>: the combination of a and b (either order; "" in the file is no edge, so an edge can
    /// combine with none) for this height step, else the one without a step; else b without a, a without b, none for
    /// two.</summary>
    public string? Combine(string? a, string? b, int heightStep)
    {
        string ka = a?.ToLowerInvariant() ?? "", kb = b?.ToLowerInvariant() ?? "";
        foreach (int? step in (int?[])[heightStep, null])
            if (_pairs.TryGetValue((ka, kb, step), out var r) || _pairs.TryGetValue((kb, ka, step), out r)) return r;
        return a is not null && b is not null ? null : a ?? b;
    }
}
