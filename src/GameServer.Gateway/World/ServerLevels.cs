using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Core;
using LeagueCategory = GameServer.Core.World.LeagueCategory;

/// <summary>Server-side level generation (GameServer.Data/Terrain/Generation, Agents/docs/re/011-level-generation.md):
/// for an area the port reproduces 1:1, the Gateway generates the layout itself from the seed instead of reading the
/// client's capture: the walkability grid (doodad collision included), every cell's ground height, and the area
/// transitions and waypoint. Supported areas are world.db's server_level_areas rows with generated = 1 (taken from the
/// level generator's gate, scripts/re/levelgen/gate-baseline.json, by a world update). Needs the game
/// files extracted to data/game-files.zip (<see cref="GamePack"/>); Game_SERVER_LEVELS=0 turns it off.</summary>
public static class ServerLevels
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
        /// <summary>The Labyrinth's objects its tiles place (server-only, as the room lines): the ElegantHouse doorways'
        /// roller traps, the abyss edges' blades, the smashable and hidden walls, the statues that wake.</summary>
        public IReadOnlyList<PlacedTileObject> TileLabyrinthObjects { get; init; } = [];
        /// <summary>The server objects of the placed rooms and the objects their point lists name (AreaObjects.RoomObjects):
        /// the quest engine's places for room objects data/room-points.json does not keep (QuestObjectTypes.AreaPlacements).</summary>
        public IReadOnlyList<PlacedTileObject> RoomObjects { get; init; } = [];
        /// <summary>The rooms' links between <see cref="RoomObjects"/> (indexes in it): the Labyrinth's puzzle wiring.</summary>
        public IReadOnlyList<PlacedLink> RoomLinks { get; init; } = [];
        /// <summary>The placed rooms that have pack points, with those points (<see cref="AreaPopulation.PackRooms"/>).</summary>
        public IReadOnlyList<AreaPopulation.PlacedRoom> PackRooms { get; init; } = [];
        public long Milliseconds { get; init; }
        /// <summary>The generator's subgraph records with their placed rooms (each room's graph node descriptions).</summary>
        public IReadOnlyList<DoodadRecord> Records { get; init; } = [];
        /// <summary>The cell rectangles (x0, y0, x1, y1; end exclusive) of the records an ExtraTerrainFeatures row added
        /// as a sub-area (<see cref="DoodadRecord.FeatureWorldArea"/>, the Sacred Grove beside a map).</summary>
        public IReadOnlyList<(int X0, int Y0, int X1, int Y1)> FeatureAreas { get; init; } = [];
        /// <summary>Every area transition object of the layout with its role from its room's graph node
        /// (<see cref="AreaExits"/>): what <see cref="AreaTransitionCatalog"/> creates.</summary>
        public IReadOnlyList<PlacedExit> Exits { get; init; } = [];
        /// <summary>Where each "entranceN" of the layout is (an arrival point, also for one-way entrances).</summary>
        public IReadOnlyList<EntrancePoint> Entrances { get; init; } = [];
        /// <summary>Where the player arrives when a terrain plugin marks a start room (<see cref="AreaObjects.Start"/>);
        /// null for every other area.</summary>
        public (int X, int Y, float Angle)? Start { get; init; }
    }

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
    // The area stats an (area, seed) is played with (0x1d's first list, 1-based Stats ids; InstanceEvents.AreaStats): the
    // generator's extra terrain feature roll reads them as the client's does, so the level is generated with the same list.
    // Part of the cache key (StatsKey): two instances of one (area, seed) with other stats are other levels.
    static readonly ConcurrentDictionary<(string Area, uint Seed), IReadOnlyDictionary<uint, int>> PlayedStats = new();
    // A seed's rules cannot change after any client has received it. Reserve distinct seeds for conflicting rules.
    // The league category an (area, seed) is played in (0x1d +0x28, InstanceEvents.LeagueCategory): the client picks the
    // topology by it (the Deepwater variants of The Coast at 59), so the level is generated with the same. Part of the cache
    // key (StatsKey) when it is not 60.
    static readonly ConcurrentDictionary<(string Area, uint Seed), int> PlayedCategories = new();
    // The terrain plugin states an (area, seed) is played with (0x1d extra data, InstanceEvents.TerrainPluginStates): the
    // client's plugin builds its part of the level from them. Part of the cache key (StatsKey).
    static readonly ConcurrentDictionary<(string Area, uint Seed), IReadOnlyDictionary<ushort, byte[]>> PlayedPluginStates = new();
    static readonly Dictionary<(string Area, uint Seed), (int Level, string Stats, int Category, string States)> PlayedContexts = new();
    static readonly ConcurrentDictionary<uint, byte[]> DelveTerrains = new();

    public static byte[]? DelveTerrain(uint seed) => DelveTerrains.GetValueOrDefault(seed);
    public static Level? PrepareDelve(uint seed, byte level, byte[] terrain)
    {
        if (!DelveTerrains.TryAdd(seed, (byte[])terrain.Clone())) return null;
        PlayedLevels[("Delve_Main", seed)] = level;
        var generated = For("Delve_Main", seed, level);
        if (generated is null) ForgetDelve(seed);
        return generated;
    }
    public static void ForgetDelve(uint seed)
    {
        DelveTerrains.TryRemove(seed, out _);
        PlayedLevels.TryRemove(("Delve_Main", seed), out _);
        foreach (var key in Generated.Keys.Where(k => k.Area == "Delve_Main" && k.Seed == seed))
        { Generated.TryRemove(key, out _); Live.TryRemove(key, out _); Errors.TryRemove(key, out _); }
    }

    /// <summary>The exception of a failed generation of (area, seed), while it is among the last ones asked for.</summary>
    public static string? Error(string areaId, uint seed) =>
        Errors.GetValueOrDefault((CacheArea(areaId, LabyrinthDay.NodeFor(areaId, seed)) + StatsKey(areaId, seed), seed, LevelOf(areaId, seed, 0)));

    /// <summary>The cache's area key: a Labyrinth room played in today's layout is generated with its labyrinth node
    /// (LabyrinthDay), so the day's seed is part of it and a new day generates the room again.</summary>
    static string CacheArea(string areaId, LabyrinthNodeKey? node) => node is null ? areaId : $"{areaId}@lab{node.Seed}";

    /// <summary>The cache key's part for the area stats and league category (area, seed) is played with: none without
    /// stats and at category 60.</summary>
    static string StatsKey(string areaId, uint seed) =>
        (PlayedStats.TryGetValue((areaId, seed), out var stats) && stats.Count > 0
            ? "#" + string.Join(",", stats.OrderBy(s => s.Key).Select(s => $"{s.Key}={s.Value}")) : "")
        + (CategoryOf(areaId, seed) is var category and not LeagueCategory.None ? $"@c{category}" : "")
        + (PluginStatesOf(areaId, seed) is { } states ? "~p" + StatesKey(states) : "");

    /// <summary>The terrain plugin states (area, seed) is played with, null for none.</summary>
    public static IReadOnlyDictionary<ushort, byte[]>? PluginStatesOf(string areaId, uint seed) =>
        PlayedPluginStates.TryGetValue((areaId, seed), out var states) && states.Count > 0 ? states : null;

    // A short stable key of the states: the first 8 hex digits of SHA-256 over "id:hex;" per plugin, in id order.
    static string StatesKey(IReadOnlyDictionary<ushort, byte[]>? states) => states is not { Count: > 0 } ? "" :
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(
            string.Concat(states.OrderBy(p => p.Key).Select(p => $"{p.Key}:{Convert.ToHexString(p.Value)};")))))[..8];

    /// <summary>(area, seed) is played without the terrain plugin states <see cref="PlayableSeed(string, uint, int, IReadOnlyDictionary{uint, int}?, int, IReadOnlyList{ValueTuple{ushort, byte[]}}?)"/>
    /// recorded: 0x1d carries other extra data in their place, so the client builds the level without them.</summary>
    public static void PlayWithoutPluginStates(string areaId, uint seed)
    {
        lock (Gate)
        {
            PlayedPluginStates.TryRemove((areaId, seed), out _);
            if (PlayedContexts.TryGetValue((areaId, seed), out var played)) PlayedContexts[(areaId, seed)] = played with { States = "" };
        }
    }

    /// <summary>(area, seed) is played at <paramref name="areaLevel"/> (a module's AreaHeader.Level override):
    /// updates the recorded level so <see cref="For(string, uint)"/> matches what 0x1d sends.</summary>
    public static void PlayAtLevel(string areaId, uint seed, int areaLevel)
    {
        if (areaLevel <= 0) return;
        lock (Gate)
        {
            PlayedLevels[(areaId, seed)] = areaLevel;
            if (PlayedContexts.TryGetValue((areaId, seed), out var played))
                PlayedContexts[(areaId, seed)] = played with { Level = areaLevel };
        }
    }

    static int CategoryOf(string areaId, uint seed) => PlayedCategories.GetValueOrDefault((areaId, seed), LeagueCategory.None);

    /// <summary>The area level (area, seed) is generated at: <paramref name="areaLevel"/> when given (above 0), else the
    /// one it was played at (<see cref="PlayedLevels"/>), else its WorldAreas level (what the client takes for a 0x1d
    /// level of 0).</summary>
    static int LevelOf(string areaId, uint seed, int areaLevel) =>
        areaLevel > 0 ? areaLevel : PlayedLevels.TryGetValue((areaId, seed), out int played) ? played : AreaLevel(areaId);

    /// <summary>Whether the Gateway generates this area itself (<see cref="Supported"/>).</summary>
    public static bool IsSupported(string areaId) => Enabled && Supported.Contains(areaId);

    /// <summary>The areas the Gateway generates itself: world.db's server_level_areas rows with generated = 1, read once
    /// (Auth reads the same rows at its startup).</summary>
    public static IReadOnlySet<string> Supported
    {
        get
        {
            var supported = _supported;
            if (supported is null) lock (SupportedGate) { LoadAreas(); supported = _supported!; }
            return supported;
        }
    }

    /// <summary>The seed played in place of seed 1 for an area whose seed-1 load the client fails too, or null
    /// (server_level_areas.seed_in_place_of_one; Auth gives new instances the same).</summary>
    public static uint? SeedInPlaceOfOne(string areaId)
    {
        lock (SupportedGate)
        {
            LoadAreas();
            return _inPlaceOfOne!.TryGetValue(areaId, out uint seed) ? seed : null;
        }
    }

    /// <summary>Drops the lists read from world.db, so the next check reads them again (tests).</summary>
    public static void Reload() { lock (SupportedGate) { _supported = null; _inPlaceOfOne = null; } }

    // Under SupportedGate. The Gateway's open world.db, or (a tool without it) data/world.db opened for the read.
    static void LoadAreas()
    {
        if (_supported is not null && _inPlaceOfOne is not null) return;
        IReadOnlyList<GameServer.Storage.GameDataStore.ServerLevelAreaRow> rows;
        try
        {
            if (GameData.Store is { } store) rows = store.LoadServerLevelAreas();
            else
            {
                using var opened = new GameServer.Storage.GameDataStore(GameData.DefaultPath);
                rows = opened.LoadServerLevelAreas();
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] server levels: world.db: {ex.Message}");
            rows = [];
        }
        _inPlaceOfOne = rows.Where(r => r.SeedInPlaceOfOne is not null).ToDictionary(r => r.AreaId, r => r.SeedInPlaceOfOne!.Value, StringComparer.Ordinal);
        _supported = rows.Where(r => r.Generated).Select(r => r.AreaId).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The generated layout of (area, seed), or null when the area is not supported or generation failed
    /// (the Gateway then falls back to the capture). Generated once per (area, seed) and kept while among the
    /// <see cref="Kept"/> most recently asked for, or while a layout in use holds it. Lock-free unless it generates.
    /// <paramref name="areaLevel"/>: the 0x1d level the instance is played at (0: none sent, <see cref="LevelOf"/>).</summary>
    public static Level? For(string areaId, uint seed, int areaLevel)
    {
        if (!IsSupported(areaId) && !(areaId == "Delve_Main" && DelveTerrains.ContainsKey(seed))) return null;
        int at = LevelOf(areaId, seed, areaLevel);
        var node = LabyrinthDay.NodeFor(areaId, seed);
        var key = (CacheArea(areaId, node) + StatsKey(areaId, seed), seed, at);
        if (Known(key, out var known)) return known;
        lock (Gate)
        {
            if (Known(key, out known)) return known;
            var level = Generate(areaId, seed, at, node);
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
            if (!weak.TryGetTarget(out _))
            {
                Live.TryRemove(key, out _);
                if (key.Area == "Delve_Main" && !Generated.ContainsKey(key))
                { DelveTerrains.TryRemove(key.Seed, out _); PlayedLevels.TryRemove(("Delve_Main", key.Seed), out _); }
            }
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
    public static uint PlayableSeed(string areaId, uint seed, int areaLevel) => PlayableSeed(areaId, seed, areaLevel, null);

    /// <inheritdoc cref="PlayableSeed(string, uint)"/>
    /// <param name="stats">The area stats the instance is played with (0x1d's first list), which the generation reads; a
    /// seed tried in place of one the port cannot generate is tried with them too.</param>
    public static uint PlayableSeed(string areaId, uint seed, int areaLevel, IReadOnlyDictionary<uint, int>? stats) =>
        PlayableSeed(areaId, seed, areaLevel, stats, LeagueCategory.None);

    /// <inheritdoc cref="PlayableSeed(string, uint, int, IReadOnlyDictionary{uint, int}?)"/>
    /// <param name="category">The league category the instance is played in (0x1d +0x28, 60 for none), which the
    /// topology pick reads.</param>
    public static uint PlayableSeed(string areaId, uint seed, int areaLevel, IReadOnlyDictionary<uint, int>? stats, int category) =>
        PlayableSeed(areaId, seed, areaLevel, stats, category, null);

    /// <inheritdoc cref="PlayableSeed(string, uint, int, IReadOnlyDictionary{uint, int}?, int)"/>
    /// <param name="pluginStates">The terrain plugin states 0x1d's extra data sends (TerrainPlugins <c>__col1</c>, bytes),
    /// which the area's plugin builds its part of the level from.</param>
    public static uint PlayableSeed(string areaId, uint seed, int areaLevel, IReadOnlyDictionary<uint, int>? stats, int category,
        IReadOnlyList<(ushort Plugin, byte[] State)>? pluginStates)
    {
        category = LeagueCategory.Clamp(category);
        var states = (pluginStates ?? []).ToDictionary(p => p.Plugin, p => p.State);
        string statesKey = StatesKey(states);
        lock (Gate)
        {
            var snapshot = stats is null ? new Dictionary<uint, int>()
                : stats.Where(p => p.Value != 0).ToDictionary(p => p.Key, p => p.Value);
            string statsKey = string.Join(",", snapshot.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
            int level = areaLevel > 0 ? areaLevel : AreaLevel(areaId);
            bool exact = AreaLayouts.Recording || Environment.GetEnvironmentVariable("Game_SEED") is { Length: > 0 };
            if (!exact && seed == 1 && SeedInPlaceOfOne(areaId) is { } seedOneReplacement) seed = seedOneReplacement;
            uint Reserve(uint candidate)
            {
                while (PlayedContexts.TryGetValue((areaId, candidate), out var prior) && prior != (level, statsKey, category, statesKey))
                    candidate = Replacements(candidate).First();
                PlayedContexts[(areaId, candidate)] = (level, statsKey, category, statesKey);
                PlayedPluginStates[(areaId, candidate)] = states;
                PlayedCategories[(areaId, candidate)] = category;
                PlayedLevels[(areaId, candidate)] = level;
                PlayedStats[(areaId, candidate)] = snapshot;
                return candidate;
            }
            seed = Reserve(seed);
            if (exact || !IsSupported(areaId) || For(areaId, seed, level) is not null) return seed;
            foreach (uint replacement in Replacements(seed))
            {
                uint other = Reserve(replacement);
                if (For(areaId, other, level) is null) continue;
                Console.WriteLine($"[gateway] server levels: {areaId} seed {seed} not generated; played at seed {other}");
                return other;
            }
            return seed;
        }
    }

    /// <summary>The seeds tried in place of one the port cannot generate, in order (1 … int.MaxValue, as Auth's).</summary>
    public static IEnumerable<uint> Replacements(uint seed)
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

    static Level? Generate(string areaId, uint seed, int areaLevel, LabyrinthNodeKey? node)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var files = Files();
            var area = AreaInfo.Load(Read, areaId, areaLevel);
            if (PlayedStats.TryGetValue((areaId, seed), out var played) && played.Count > 0)
                area = area with { Stats = played.ToDictionary(s => (int)s.Key, s => s.Value) };
            int category = CategoryOf(areaId, seed);
            var pluginStates = PluginStatesOf(areaId, seed);
            var result = LevelGenerator.Generate(files, area, seed, topologyVariant: category, labyrinthNode: node,
                delveTerrain: areaId == "Delve_Main" ? DelveTerrain(seed) : null, pluginStates: pluginStates);
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
            var start = AreaObjects.Start(result);
            Console.WriteLine($"[gateway] server levels: generated {areaId} seed {seed} level {areaLevel}{(node is null ? "" : $" labyrinth node {node.Row} of day {node.Seed}")}{(area.Stats is { Count: > 0 } s ? $" with {s.Count} area stat(s)" : "")}{(category == LeagueCategory.None ? "" : $" league category {category}")}{(pluginStates is null ? "" : $" with {pluginStates.Count} terrain plugin state(s)")} ({walk.Width}x{walk.Height} cells, " +
                $"{objects.Count} tile object(s), {doodads.Placed.Count} doodads){(start is { } st ? $", start ({st.X}, {st.Y})" : "")}, tile hash {result.Grid.Hash()}, in {clock.ElapsedMilliseconds} ms");
            ConnectionPerformance.Server("LevelGen", clock.Elapsed, areaId);
            var roomLinks = new List<PlacedLink>();
            return new Level(walk.Width, walk.Height, walk.Walk, heights, objects, doodads.Placed, AreaObjects.Rooms(result),
                AreaObjects.QuestObjects(files, result), walk.Projectile)
            {
                TileHash = result.Grid.Hash(), Milliseconds = clock.ElapsedMilliseconds, Records = result.Records,
                Exits = exits.Exits, Entrances = exits.Entrances, Start = start,
                TileQuestObjects = AreaObjects.TileObjects(files, result, QuestObjectTypes.IsTileQuestObject),
                TileLabyrinthObjects = AreaObjects.TileObjects(files, result, t => t.StartsWith("Metadata/Terrain/Labyrinth/", StringComparison.Ordinal)
                    || t.StartsWith(LabyrinthTrials.StatueFolder, StringComparison.Ordinal)),
                RoomObjects = AreaObjects.RoomObjects(result, points: true, links: roomLinks),
                RoomLinks = roomLinks,
                PackRooms = AreaPopulation.PackRooms(result.Records),
                FeatureAreas = [.. result.Records.Where(r => r.FeatureWorldArea >= 0).Select(r => (r.X * 23, r.Y * 23, (r.X + r.W) * 23, (r.Y + r.H) * 23))],
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gateway] server levels: {areaId} seed {seed} level {areaLevel} not generated ({ex.GetType().Name}: {ex.Message.Split('\n')[0]})");
            Errors[(CacheArea(areaId, node) + StatsKey(areaId, seed), seed, areaLevel)] = ex.ToString();
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
            foreach (var row in ClientTables.Table("WorldAreas"))
                levels[row.Text("Id") ?? ""] = row.Int("AreaLevel");
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or KeyNotFoundException) { }
        return levels;
    });

    // The WorldAreas row's area level (the generator's AreaInfo reads level-dependent rows); 1 without the table.
    public static int AreaLevel(string areaId) => Math.Max(1, Levels.Value.GetValueOrDefault(areaId, 1));
}
