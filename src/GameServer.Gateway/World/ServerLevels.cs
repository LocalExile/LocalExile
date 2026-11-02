using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;

/// <summary>Server-side level generation (GameServer.Data/Terrain/Generation, Agents/docs/re/011-level-generation.md):
/// for an area the port reproduces 1:1, the Gateway generates the layout itself from the seed instead of reading the
/// client's capture: the walkability grid (doodad collision included), every cell's ground height, and the area
/// transitions and waypoint. Supported areas are the ones whose seed-1 trace passes every check of the level
/// generator's gate (scripts/re/levelgen/gate-baseline.json, committed with each generator change), or whose seed-1
/// failure the port reproduces (the client cannot build it either), and whose logged loads all match. Needs the game
/// files extracted to data/game-files.zip (<see cref="GamePack"/>); Game_SERVER_LEVELS=0 turns it off.</summary>
static class ServerLevels
{
    public const string UnsupportedMessage = "This map is generated on client-side and is not supported yet by the Server.";

    /// <summary>A generated layout: Width × Height cells, one walk value (0 … 5, walkable from 2) and one ground height
    /// (× 7.8125 for world units) per cell, the tile objects the server creates (area transitions, the waypoint), and
    /// the client's doodads and placed .arm rooms (what the recorder's .doodads.json and .rooms.json hold: the rooms'
    /// monster points and quest objects are fitted to them), the quest tile objects (its .objects.json) and the
    /// projectile grid (its .projectile.bin: 0 stops projectiles).</summary>
    public sealed record Level(int Width, int Height, byte[] Walk, short[] Heights, IReadOnlyList<PlacedTileObject> Objects,
        IReadOnlyList<PlacedDoodad> Doodads, IReadOnlyList<PlacedArmRoom> Rooms, IReadOnlyList<PlacedTileObject> QuestObjects,
        byte[] Projectile)
    {
        /// <summary>The client's tile hash of the generated grid, and how long the generation took.</summary>
        public uint TileHash { get; init; }
        /// <summary>Every quest tile object of the grid (<see cref="QuestObjectTypes.IsTileQuestObject"/>): the quest engine's
        /// seals, levers, doors, altars and quest chests. <see cref="QuestObjects"/> keeps a1q4's types only, as the
        /// recorder captures them (the level generator's gate compares those).</summary>
        public IReadOnlyList<PlacedTileObject> TileQuestObjects { get; init; } = [];
        /// <summary>The server objects of the placed rooms and the objects their point lists name (AreaObjects.RoomObjects):
        /// the quest engine's places for room objects data/room-points.json does not keep (QuestObjectTypes.AreaPlacements).</summary>
        public IReadOnlyList<PlacedTileObject> RoomObjects { get; init; } = [];
        public long Milliseconds { get; init; }
        /// <summary>The generator's subgraph records with their placed rooms (each room's graph node descriptions).</summary>
        public IReadOnlyList<DoodadRecord> Records { get; init; } = [];
        /// <summary>Every area transition object of the layout with its role from its room's graph node
        /// (<see cref="AreaExits"/>): what <see cref="AreaTransitionCatalog"/> creates.</summary>
        public IReadOnlyList<PlacedExit> Exits { get; init; } = [];
        /// <summary>Where each "entranceN" of the layout is (an arrival point, also for one-way entrances).</summary>
        public IReadOnlyList<EntrancePoint> Entrances { get; init; } = [];
    }

    /// <summary>Where the supported list comes from (the repository root is the Gateway's directory; tests replace it).</summary>
    public static string BaselineFile { get; set; } = GameServer.Core.World.ServerLevelAreas.BaselineFile;

    static readonly bool Enabled = Environment.GetEnvironmentVariable("Game_SERVER_LEVELS") != "0";
    static readonly object Gate = new();
    static volatile HashSet<string>? _supported;
    static Dictionary<string, uint>? _inPlaceOfOne;
    static readonly Lock SupportedGate = new();
    static TerrainCache? _files;
    // The levels kept for the next entry, with when each was last asked for (a timestamp written without a lock: the
    // projectile and height lookups ask on every step). With random seeds every new instance is a new (area, seed), so
    // only the Kept most recently used stay (a level's grids are ~11 MB: 24 of them were 265 MB in the perf benchmark);
    // an older one is generated again when asked for (0.2 s median, 0.6 s at most there; performance.md). A failed
    // generation (null) is kept too, so it is not retried, and goes first.
    sealed class KeptLevel(Level? level)
    {
        public readonly Level? Level = level;
        public long UsedAt = Stopwatch.GetTimestamp();
    }
    static readonly ConcurrentDictionary<(string Area, uint Seed, int AreaLevel), KeptLevel> Generated = new();
    // Every level still in use: a connection's AreaLayout holds its level (AreaLayout.Level), so a level stays
    // available, and is never generated again, while anybody plays it, however many others are generated meanwhile.
    static readonly ConcurrentDictionary<(string Area, uint Seed, int AreaLevel), WeakReference<Level>> Live = new();
    const int Kept = 8;
    // Why the last generations that failed did (the exception in full), for the capture tour's records.
    static readonly ConcurrentDictionary<(string Area, uint Seed, int AreaLevel), string> Errors = new();
    // The area level an (area, seed) is played at when 0x1d sends one (an opened map's item level): the client generates
    // at it, and the topology and room picks depend on it (MapWorldsChateau seed 1174023763 is chateau_1.dgr at its
    // WorldAreas level 71, chateau_2.dgr at a tier-16 map's 83). Lookups without a level (monster packs, heights,
    // projectiles) get that generation. Never trimmed: one entry per map instance opened.
    static readonly ConcurrentDictionary<(string Area, uint Seed), int> PlayedLevels = new();

    /// <summary>The exception of a failed generation of (area, seed), while it is among the last ones asked for.</summary>
    public static string? Error(string areaId, uint seed) => Errors.GetValueOrDefault((areaId, seed, LevelOf(areaId, seed, 0)));

    /// <summary>The area level (area, seed) is generated at: <paramref name="areaLevel"/> when given (above 0), else the
    /// one it was played at (<see cref="PlayedLevels"/>), else its WorldAreas level (what the client takes for a 0x1d
    /// level of 0).</summary>
    static int LevelOf(string areaId, uint seed, int areaLevel) =>
        areaLevel > 0 ? areaLevel : PlayedLevels.TryGetValue((areaId, seed), out int played) ? played : AreaLevel(areaId);

    /// <summary>Whether the Gateway generates this area itself (<see cref="BaselineFile"/>).</summary>
    public static bool IsSupported(string areaId)
    {
        if (!Enabled) return false;
        var supported = _supported;
        if (supported is null)
            lock (SupportedGate) supported = _supported ??= LoadSupported(BaselineFile);
        return supported.Contains(areaId);
    }

    /// <summary>The seed played in place of seed 1 for an area whose seed-1 load the client fails too, or null
    /// (<see cref="GameServer.Core.World.ServerLevelAreas.SeedsInPlaceOfOne"/>; Auth gives new instances the same).</summary>
    public static uint? SeedInPlaceOfOne(string areaId)
    {
        lock (SupportedGate)
            return (_inPlaceOfOne ??= GameServer.Core.World.ServerLevelAreas.SeedsInPlaceOfOne(BaselineFile,
                m => Console.WriteLine($"[gateway] server levels: {m}"))).TryGetValue(areaId, out uint seed) ? seed : null;
    }

    /// <summary>Drops the lists read from the baseline, so the next check reads <see cref="BaselineFile"/> again (tests).</summary>
    internal static void Reload() { lock (SupportedGate) { _supported = null; _inPlaceOfOne = null; } }

    /// <summary>The supported areas of a gate baseline (<see cref="GameServer.Core.World.ServerLevelAreas.Load"/>).</summary>
    internal static HashSet<string> LoadSupported(string file) =>
        GameServer.Core.World.ServerLevelAreas.Load(file, m => Console.WriteLine($"[gateway] server levels: {m}"));

    /// <summary>The generated layout of (area, seed), or null when the area is not supported or generation failed
    /// (the Gateway then falls back to the capture). Generated once per (area, seed) and kept while among the
    /// <see cref="Kept"/> most recently asked for, or while a layout in use holds it. Lock-free unless it generates.
    /// <paramref name="areaLevel"/>: the 0x1d level the instance is played at (0: none sent, <see cref="LevelOf"/>).</summary>
    public static Level? For(string areaId, uint seed, int areaLevel)
    {
        if (!IsSupported(areaId)) return null;
        int at = LevelOf(areaId, seed, areaLevel);
        var key = (areaId, seed, at);
        if (Known(key, out var known)) return known;
        lock (Gate)
        {
            if (Known(key, out known)) return known;
            var level = Generate(areaId, seed, at);
            Generated[key] = new(level);
            if (level is not null) Live[key] = new(level);
            Trim(key);
            return level;
        }
    }

    /// <summary><see cref="For(string, uint, int)"/> at the level (area, seed) is played at. A separate overload, not an
    /// optional parameter: modules built against this signature keep binding to it.</summary>
    public static Level? For(string areaId, uint seed) => For(areaId, seed, 0);

    static bool Known((string Area, uint Seed, int AreaLevel) key, out Level? level)
    {
        if (Generated.TryGetValue(key, out var kept))
        {
            kept.UsedAt = Stopwatch.GetTimestamp();
            level = kept.Level;
            return true;
        }
        level = Live.TryGetValue(key, out var weak) && weak.TryGetTarget(out var live) ? live : null;
        return level is not null;
    }

    // Under Gate: the kept levels beyond Kept go, older failed generations first, then the least recently used; never the
    // one just generated (its callers ask again at once). A level in use stays reachable through Live. Live forgets the
    // levels nobody holds any more.
    static void Trim((string Area, uint Seed, int AreaLevel) newest)
    {
        while (Generated.Count > Kept)
        {
            var oldest = Generated.Where(e => e.Key != newest)
                .OrderBy(e => e.Value.Level is null ? 0 : 1).ThenBy(e => e.Value.UsedAt).First().Key;
            Generated.TryRemove(oldest, out _);
            Errors.TryRemove(oldest, out _);
        }
        foreach (var (key, weak) in Live)
            if (!weak.TryGetTarget(out _)) Live.TryRemove(key, out _);
    }

    /// <summary>The seed an instance is played at (sent in 0x1d). Seed 1 of an area whose seed-1 load the client fails
    /// too (The Crossroads, Act 6's Twilight Strand, …) is <see cref="SeedInPlaceOfOne"/>, a seed the client built,
    /// also when the Gateway does not generate levels, and is then treated as any other seed. A seed of a supported area
    /// is its own, unless the port
    /// cannot generate it (a few random seeds per area, e.g. 1_1_2 at 32124476: "Mismatching ground types"), then the
    /// first of <see cref="Replacements"/> it can. Without a level the Gateway would have no walk map, area transitions
    /// or waypoint while the client still builds the map. The replacement depends only on the seed, so everyone entering
    /// the instance gets the same one. Kept as it is for the capture tour (<see cref="AreaLayouts.Recording"/>) and
    /// Game_SEED, which ask for that seed's trace. A 0x1d <paramref name="areaLevel"/> above 0 is remembered for the
    /// seed played (<see cref="PlayedLevels"/>), so its layout is the one the client generates.</summary>
    public static uint PlayableSeed(string areaId, uint seed) => PlayableSeed(areaId, seed, 0);

    /// <inheritdoc cref="PlayableSeed(string, uint)"/>
    public static uint PlayableSeed(string areaId, uint seed, int areaLevel)
    {
        seed = Playable(areaId, seed, areaLevel);
        if (areaLevel > 0) PlayedLevels[(areaId, seed)] = areaLevel;
        return seed;
    }

    static uint Playable(string areaId, uint seed, int areaLevel)
    {
        if (AreaLayouts.Recording || Environment.GetEnvironmentVariable("Game_SEED") is { Length: > 0 }) return seed;
        if (seed == 1)
        {
            if (SeedInPlaceOfOne(areaId) is not { } inPlace) return seed;
            Console.WriteLine($"[gateway] server levels: the client cannot generate {areaId} at seed 1; played at seed {inPlace}");
            seed = inPlace;
        }
        if (!IsSupported(areaId) || For(areaId, seed, areaLevel) is not null) return seed;
        foreach (uint other in Replacements(seed))
            if (For(areaId, other, areaLevel) is not null)
            {
                Console.WriteLine($"[gateway] server levels: {areaId} seed {seed} not generated; played at seed {other}");
                return other;
            }
        return seed;
    }

    /// <summary>The seeds tried in place of one the port cannot generate, in order (1 … int.MaxValue, as Auth's).</summary>
    internal static IEnumerable<uint> Replacements(uint seed)
    {
        for (uint k = 1; k <= 8; k++)
        {
            uint other = unchecked(seed * 0x9e3779b1u + k * 0x85ebca77u) & 0x7fffffff;
            yield return other is 0 or 1 ? k + 1 : other;
        }
    }

    /// <summary>The generated layout of (area, seed) standing in for a capture missing from <paramref name="folder"/>:
    /// only for the Gateway's own capture folder (data/walkability), never for a test's.</summary>
    public static Level? InPlaceOf(string folder, string areaId, uint seed) =>
        Path.GetFullPath(folder) == Path.GetFullPath("data/walkability") ? For(areaId, seed) : null;

    static Level? Generate(string areaId, uint seed, int areaLevel)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var files = Files();
            var area = AreaInfo.Load(Read, areaId, areaLevel);
            var result = LevelGenerator.Generate(files, area, seed);
            var walk = Walkability.Build(result.Grid, t => files.Walk(t.Path));
            // The doodad stage stamps the doodads' collision into the grids (re/011 §6c).
            var doodads = new DoodadStage(files, result.Random, result.Grid, walk, []);
            doodads.Run(result.Records);
            var heights = CellHeights.Build(result.Grid, t => files.Walk(t.Path));
            var objects = AreaObjects.Transitions(files, result);
            // An object counts as an exit only if a player can walk up to it: a walkable cell within the walk-up range
            // of an area transition (PlayerWalk.InWalkUpRange, size 5: 13 cells).
            var exits = AreaExits.Resolve(files, result, (x, y) => WithinReach(walk.Walk, walk.Width, walk.Height, x, y, 13));
            // A room's waypoint line when no tile places one (The Ledge).
            if (!objects.Any(o => o.Type == GameServer.Core.Protocol.Instance.WaypointObject.MetadataPath)) objects.AddRange(AreaObjects.RoomWaypoints(result));
            Console.WriteLine($"[gateway] server levels: generated {areaId} seed {seed} level {areaLevel} ({walk.Width}x{walk.Height} cells, " +
                $"{objects.Count} tile object(s), {doodads.Placed.Count} doodads) in {clock.ElapsedMilliseconds} ms");
            ConnectionPerformance.Server("LevelGen", clock.Elapsed, areaId);
            return new Level(walk.Width, walk.Height, walk.Walk, heights, objects, doodads.Placed, AreaObjects.Rooms(result),
                AreaObjects.QuestObjects(files, result), walk.Projectile)
            {
                TileHash = result.Grid.Hash(), Milliseconds = clock.ElapsedMilliseconds, Records = result.Records,
                Exits = exits.Exits, Entrances = exits.Entrances,
                TileQuestObjects = AreaObjects.TileObjects(files, result, QuestObjectTypes.IsTileQuestObject),
                RoomObjects = AreaObjects.RoomObjects(result, points: true),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gateway] server levels: {areaId} seed {seed} level {areaLevel} not generated ({ex.GetType().Name}: {ex.Message.Split('\n')[0]})");
            Errors[(areaId, seed, areaLevel)] = ex.ToString();
            return null;
        }
    }

    static bool WithinReach(byte[] cells, int w, int h, int x, int y, int r)
    {
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int cx = x + dx, cy = y + dy;
                if (dx * dx + dy * dy <= r * r && cx >= 0 && cy >= 0 && cx < w && cy < h && cells[cy * w + cx] >= 2) return true;
            }
        return false;
    }

    // Opened on the first generation and kept.
    static TerrainCache Files() => _files ??= new TerrainCache(Read);

    static byte[]? Read(string path) => GamePack.Default.TryRead(path);

    static readonly Lazy<Dictionary<string, int>> Levels = new(() =>
    {
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine("data", "tables", "WorldAreas.json")));
            foreach (var row in doc.RootElement.EnumerateArray())
                levels[row.GetProperty("Id").GetString() ?? ""] = row.GetProperty("AreaLevel").GetInt32();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        return levels;
    });

    // The WorldAreas row's area level (the generator's AreaInfo reads level-dependent rows); 1 without the table.
    static int AreaLevel(string areaId) => Math.Max(1, Levels.Value.GetValueOrDefault(areaId, 1));
}
