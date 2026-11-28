using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain;
using GameServer.Core;

/// <summary>
/// The monster packs of an area, from the extracted tables: MonsterPacks (which areas and their weights, how many
/// monsters, the pack's boss), MonsterPackEntries (the varieties and their weights) and WorldAreas (the area level and
/// its unique boss, Bosses_MonsterVarietiesKeys). Packs with a weight for the area are scattered over the walk map;
/// packs named "&lt;area&gt;-…" (weight 0) stand at the room points of the layout that name them ("%area%-cannibalfire"
/// → "1_1_2-cannibalfire"); the room point "mapboss" takes the area's boss with the pack "&lt;area&gt;-bosspack". A map's
/// area (Maps.dat Regular_WorldAreasKey) also gets the map's own packs (Maps.dat MonsterPacksKeys: Beach → atlas-crab1),
/// which no MonsterPacks row lists by area, and the shared map pool (<see cref="AreaPacks.Pool"/>).
/// Agents/docs/protocol/14-monster-packs.md.
/// </summary>
public sealed class PackCatalog
{
    readonly string folder;

    public PackCatalog(string folder = "data")
    {
        this.folder = folder;
        _data = Load(folder);
        VarietyRows = LoadVarietyRows();
        tagRows = LoadTagRows();
    }

    public sealed record Pack(int Row, string Id, int MinCount, int MaxCount, int BossChance, int BossCount,
        IReadOnlyList<int> BossVarieties, IReadOnlyList<(int Variety, int Weight)> Entries, IReadOnlyDictionary<int, int> AreaWeights)
    {
        /// <summary>MonsterPackEntries rows with Flag = 0: exact fixed member counts (Weight = count).</summary>
        public IReadOnlyList<(int Variety, int Count)> FixedEntries { get; init; } = [];
        /// <summary>MonsterPackEntries rows with Flag != 0: weighted random roll pool.</summary>
        public IReadOnlyList<(int Variety, int Weight)> RandomEntries { get; init; } = [];
        /// <summary>MonsterPacks.__col1: total fixed member count.</summary>
        public int FixedCount { get; init; }
        /// <summary>MonsterPacks.__col8: true for named mini-boss packs whose boss spawns at Unique rarity.</summary>
        public bool UniqueBossPack { get; init; }
        /// <summary>MonsterPacks.Grounds: required tile ground names (empty = any ground).</summary>
        public IReadOnlyList<string> RequiredGrounds { get; init; } = [];
        /// <summary>MonsterPacks.__col19: excluded tile ground names.</summary>
        public IReadOnlyList<string> ExcludedGrounds { get; init; } = [];
        /// <summary>MonsterPacks.Tags (Tags rows): the terrain a shared-pool pack suits; empty = anywhere.</summary>
        public IReadOnlyList<int> Tags { get; init; } = [];

        public int PickVariety(Random rng)
        {
            var pool = RandomEntries.Count > 0 ? RandomEntries : Entries;
            int roll = rng.Next(Math.Max(1, pool.Sum(e => e.Weight)));
            foreach (var (variety, weight) in pool) if ((roll -= weight) < 0) return variety;
            return pool[^1].Variety;
        }
    }

    /// <summary>An area's packs: the scattered ones with their weights, the ones room points name (by the name after
    /// "&lt;area&gt;-"), and the unique bosses.</summary>
    public sealed record AreaPacks(string AreaId, int AreaLevel, IReadOnlyList<(Pack Pack, int Weight)> Scattered,
        IReadOnlyDictionary<string, Pack> Named, IReadOnlyList<int> Bosses)
    {
        /// <summary>The shared map pool this map area may draw from, with each pack's own weight: the chaos-* packs
        /// registered to the pool area, which no map lists by area, already cut to the ones whose terrain suits this
        /// map. Empty for every area that is not a map. An instance takes only <see cref="AreaPopulation.MapPoolPacks"/>
        /// of them (<see cref="AreaPopulation.ForInstance"/>), so a map has a handful of pack types as the official
        /// instances do: not one (a map whose Maps.dat list is one single-entry pack had one variety all over) and not
        /// all 338. The drawn packs stand in <see cref="Scattered"/> of the instance's own answer, whose Pool is empty.</summary>
        public IReadOnlyList<(Pack Pack, int Weight)> Pool { get; init; } = [];

        /// <summary>Places in <see cref="Bosses"/> (0-based) this instance does not place with the area: the later forms a
        /// fight brings (<see cref="AreaPopulation.LaterForms"/>) and the bosses a map boss override leaves out
        /// (<see cref="MapBossOverride"/>).</summary>
        public IReadOnlySet<int> Absent { get; init; } = new HashSet<int>();

        /// <summary>How much denser than the ordinary its scattered packs stand (the Labyrinth's: LabyrinthTrials.PackDensity).</summary>
        public double Density { get; init; } = 1;

        /// <summary>WorldAreas.Act for this area (1..10 for campaign areas, 0 otherwise).</summary>
        public int Act { get; init; }

        /// <summary>All MonsterVarieties rows listed as WorldAreas.Bosses across any WorldArea (so a cross-area __col8
        /// pack whose Id prefix is a sibling area never spawns that sibling's boss as a roaming mini-boss).</summary>
        public IReadOnlySet<int> AllWorldBosses { get; init; } = new HashSet<int>();
    }

    /// <summary>The weight of a map's own packs (Maps.dat MonsterPacksKeys), which have none for the area.</summary>
    public const int MapPackWeight = 100;

    /// <summary>Tag rows an area's AreaTypeTags names that no pack carries, and the pack tag they stand for. The pool's
    /// whole tag vocabulary is eight terrain tags (beach, dungeon, cave, forest, swamp, mountain, urban, temple) and
    /// all but three map areas name one of them in AreaTypeTags directly. Forbidden Woods names "forestdark", and every
    /// pool pack its captured official instances used carries "forest". The Carcass ("sewers") and Dry Sea ("desert_area", "desert_map_area") are left out: no capture shows which pack tag
    /// they stand for, so those two take the pool unfiltered rather than a guessed tag.</summary>
    static readonly IReadOnlyDictionary<string, string> TagAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["forestdark"] = "forest",
    };

    private readonly (List<Pack> Packs, List<(string Id, int Level, int Act, int[] Bosses, int[] TypeTags, bool Unique)> Areas, Dictionary<int, List<int>> MapPacks, HashSet<int> AllWorldBosses, int PoolArea)? _data;

    /// <summary>A map area's pool by WorldAreas row. It depends on the tables only, so the pack scan and the terrain
    /// cut behind it run once per area rather than on every <see cref="For"/> call.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyList<(Pack, int)>> _pools = new();

    public AreaPacks? For(string areaId)
    {
        if (_data is not { } d) return null;
        int row = d.Areas.FindIndex(a => a.Id == areaId);
        if (row < 0) return null;
        var scattered = new List<(Pack, int)>();
        var named = new Dictionary<string, Pack>();
        foreach (var p in d.Packs)
        {
            if (p.Entries.Count == 0) continue;
            if (p.Id.StartsWith(areaId + "-", StringComparison.Ordinal)) named[p.Id[(areaId.Length + 1)..]] = p;
            else if (p.AreaWeights.GetValueOrDefault(row) is int w and > 0) scattered.Add((p, w));
        }
        foreach (int pack in d.MapPacks.GetValueOrDefault(row) ?? [])
            if (pack < d.Packs.Count && d.Packs[pack].Entries.Count > 0 && !scattered.Any(s => s.Item1.Row == pack))
                scattered.Add((d.Packs[pack], MapPackWeight));
        // A Labyrinth room area has no packs in the data: ours (LabyrinthTrials.RoomSetPacks), each row below 0.
        double density = 1;
        if (scattered.Count == 0 && LabyrinthTrials.RoomSetPacks(areaId) is { Count: > 0 } labyrinth)
        {
            density = LabyrinthTrials.PackDensity;
            int Row(string id) => VarietyRows.GetValueOrDefault(id, -1);
            for (int i = 0; i < labyrinth.Count; i++)
            {
                var p = labyrinth[i];
                List<(int, int)> entries = [.. p.Varieties.Select(Row).Where(v => v >= 0).Select(v => (v, 1))];
                List<int> leaders = [.. (p.Leaders ?? []).Select(Row).Where(v => v >= 0)];
                if (entries.Count > 0)
                    scattered.Add((new Pack(-1 - i, "labyrinth-" + p.Id, p.Min, p.Max, leaders.Count > 0 ? 100 : 0, leaders.Count > 0 ? 1 : 0, leaders, entries,
                        new Dictionary<int, int>()), MapPackWeight));
            }
        }
        return new AreaPacks(areaId, d.Areas[row].Level, scattered, named, d.Areas[row].Bosses)
        {
            Density = density,
            Act = d.Areas[row].Act,
            AllWorldBosses = d.AllWorldBosses,
            // Only an ordinary map area draws from the shared pool: one with a Maps.dat row that WorldAreas does not
            // mark IsUniqueMapArea (The Shaper's Realm, Tower of Ordeals and The Beachhead have an empty signature list
            // and stay as they were).
            Pool = d.MapPacks.ContainsKey(row) && !d.Areas[row].Unique ? _pools.GetOrAdd(row, r => PoolFor(d, r, [.. scattered.Select(s => s.Item1.Row)])) : [],
        };
    }

    /// <summary>The shared map pool for a map area: the packs with a weight for the pool area (the chaos-* ones, which
    /// no map lists by area), without the ones the area already has, cut to the packs whose terrain suits this map: a
    /// pack whose Tags is empty stands anywhere, else one of its Tags must be an AreaTypeTags of the area (through
    /// <see cref="TagAliases"/>). The cut is dropped when the area's own tags name nothing the pool uses, so an area
    /// whose terrain tag is not in the pool's vocabulary gets the whole pool rather than only the untagged packs.
    /// The capture figures behind the rule are in 14-monster-packs.md.</summary>
    List<(Pack, int)> PoolFor((List<Pack> Packs, List<(string Id, int Level, int Act, int[] Bosses, int[] TypeTags, bool Unique)> Areas,
        Dictionary<int, List<int>> MapPacks, HashSet<int> AllWorldBosses, int PoolArea) d, int row, HashSet<int> already)
    {
        var pool = new List<(Pack, int)>();
        if (d.PoolArea < 0) return pool;
        var candidates = d.Packs.Where(p => p.Entries.Count > 0 && !already.Contains(p.Row)
            && p.AreaWeights.GetValueOrDefault(d.PoolArea) is int w && w > 0).ToList();
        var vocabulary = new HashSet<int>(candidates.SelectMany(p => p.Tags));
        var keys = new HashSet<int>();
        foreach (int tag in d.Areas[row].TypeTags)
            foreach (int resolved in ResolveTag(tag)) keys.Add(resolved);
        bool cut = keys.Overlaps(vocabulary);
        foreach (var p in candidates)
            if (!cut || p.Tags is not { Count: > 0 } tags || tags.Any(keys.Contains))
                pool.Add((p, p.AreaWeights[d.PoolArea]));
        return pool;
    }

    /// <summary>A Tags row as the pool reads it: itself, plus what <see cref="TagAliases"/> says it stands for.</summary>
    IEnumerable<int> ResolveTag(int row)
    {
        yield return row;
        if (tagRows is not { } rows) yield break;
        foreach (var (from, to) in TagAliases)
            if (rows.GetValueOrDefault(from, -1) == row && rows.GetValueOrDefault(to, -1) is int t && t >= 0)
                yield return t;
    }

    /// <summary>Tags rows by Id, for <see cref="TagAliases"/>; null where the table is missing. Read when the catalog is
    /// built, with the rest of its data.</summary>
    readonly Dictionary<string, int>? tagRows;

    static Dictionary<string, int>? LoadTagRows()
    {
        if (!ClientTables.Has("Tags")) return null;
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        int row = 0;
        foreach (var t in ClientTables.Table("Tags")) { if (t.TextOrNull("Id") is { } id) rows.TryAdd(id, row); row++; }
        return rows;
    }

    public Pack? Find(string id) => _data?.Packs.FirstOrDefault(p => p.Id == id);

    // MonsterVarieties rows by Id, for the Labyrinth's packs.
    readonly Dictionary<string, int> VarietyRows;

    Dictionary<string, int> LoadVarietyRows()
    {
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!ClientTables.Has("MonsterVarieties")) return rows;
        var doc = ClientTables.Table("MonsterVarieties");
        int row = 0;
        foreach (var v in doc) { if (v.Text("Id") is { } id) rows.TryAdd(id, row); row++; }
        return rows;
    }

    private static (List<Pack>, List<(string, int, int, int[], int[], bool)>, Dictionary<int, List<int>>, HashSet<int>, int)? Load(string folder)
    {
        if (!ClientTables.Has("MonsterPacks") || !ClientTables.Has("MonsterPackEntries") || !ClientTables.Has("WorldAreas")) return null;
        var byPack = new Dictionary<int, List<(int, int)>>();
        var fixedByPack = new Dictionary<int, List<(int, int)>>();
        var randomByPack = new Dictionary<int, List<(int, int)>>();
        foreach (var e in ClientTables.Table("MonsterPackEntries"))
        {
            if (e.IsNull("MonsterPacksKey") || e.IsNull("MonsterVarietiesKey")) continue;
            int pack = e.Int("MonsterPacksKey");
            int variety = e.Int("MonsterVarietiesKey");
            int weight = e.Int("Weight");
            int flag = e.IntOrNull("Flag") ?? 1;
            if (!byPack.TryGetValue(pack, out var list)) byPack[pack] = list = [];
            list.Add((variety, weight));
            if (flag == 0)
            {
                if (!fixedByPack.TryGetValue(pack, out var fl)) fixedByPack[pack] = fl = [];
                fl.Add((variety, weight));
            }
            else
            {
                if (!randomByPack.TryGetValue(pack, out var rl)) randomByPack[pack] = rl = [];
                rl.Add((variety, weight));
            }
        }
        static string[] CleanGrounds(string[] raw) =>
            raw.Length == 0 ? [] : [.. raw.Where(g => !string.IsNullOrWhiteSpace(g) && g != "-")];
        var result = new List<Pack>();
        foreach (var p in ClientTables.Table("MonsterPacks"))
        {
            int row = result.Count;
            // WorldAreasKeys with one weight each in __col9, or one weight for all of them (Act1-CoastCrabs: [6, 7], [100]).
            int[] areaKeys = [.. p.Ints("WorldAreasKeys")];
            int[] weights = [.. p.Ints("__col9")];
            var areaWeights = new Dictionary<int, int>();
            for (int i = 0; i < areaKeys.Length && weights.Length > 0; i++) areaWeights[areaKeys[i]] = weights[Math.Min(i, weights.Length - 1)];
            result.Add(new Pack(row, p.Text("Id") ?? "", p.Int("MinCount"), p.Int("MaxCount"),
                p.Int("BossMonsterSpawnChance"), p.Int("BossMonsterCount"),
                [.. p.Ints("BossMonster_MonsterVarietiesKeys")],
                byPack.GetValueOrDefault(row) ?? [], areaWeights)
            {
                FixedEntries = fixedByPack.GetValueOrDefault(row) ?? [],
                RandomEntries = randomByPack.GetValueOrDefault(row) ?? [],
                FixedCount = p.IntOrNull("__col1") ?? 0,
                UniqueBossPack = p.BoolOrNull("__col8") == true,
                RequiredGrounds = CleanGrounds(p.Texts("Grounds")),
                ExcludedGrounds = CleanGrounds(p.Texts("__col19")),
                Tags = [.. p.Ints("Tags")],
            });
        }
        var areaRows = new List<(string, int, int, int[], int[], bool)>();
        var allWorldBosses = new HashSet<int>();
        foreach (var a in ClientTables.Table("WorldAreas"))
        {
            int[] bosses = [.. a.Ints("Bosses_MonsterVarietiesKeys")];
            foreach (int b in bosses) allWorldBosses.Add(b);
            areaRows.Add((a.Text("Id") ?? "", a.Int("AreaLevel"), a.IntOrNull("Act") ?? 0, bosses, [.. a.Ints("AreaTypeTags")], a.BoolOrNull("IsUniqueMapArea") == true));
        }
        // Maps.dat: a map's area → its packs (optional table: without it maps have no packs of their own).
        var mapPacks = new Dictionary<int, List<int>>();
        if (ClientTables.Has("Maps"))
            foreach (var m in ClientTables.Table("Maps"))
                if (m.IntOrNull("Regular_WorldAreasKey") is int area)
                {
                    if (!mapPacks.TryGetValue(area, out var list)) mapPacks[area] = list = [];
                    list.AddRange([.. m.Ints("MonsterPacksKeys").Where(k => !list.Contains(k)).Distinct()]);
                }
        // The shared map pool's area: the WorldAreas row the maps' own signature packs name most often in their
        // WorldAreasKeys (row 168 in this copy: 873 of the 1316 signature references, and the 338 packs with a weight
        // for it are every chaos-* pack there is). Derived rather than written down so a table that moves rows keeps
        // working; -1 where Maps.dat is missing, and then a map has no pool.
        var poolVotes = new Dictionary<int, int>();
        foreach (int pack in mapPacks.Values.SelectMany(l => l))
            if (pack >= 0 && pack < result.Count)
                foreach (var (area, weight) in result[pack].AreaWeights)
                    if (weight > 0) poolVotes[area] = poolVotes.GetValueOrDefault(area) + 1;
        int poolArea = poolVotes.Count == 0 ? -1 : poolVotes.MaxBy(v => v.Value).Key;
        return (result, areaRows, mapPacks, allWorldBosses, poolArea);
    }
}

/// <summary>
/// Where an area's monsters stand and what rarity they are. The tables give the packs and their weights; how many
/// packs an area has and how often a pack is magic or has a rare are decided by the official server and are not in the
/// client, so the numbers here are our own (<see cref="CellsPerPack"/>, <see cref="MagicChance"/>, <see cref="RareChance"/>).
/// </summary>
public static class AreaPopulation
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadGameData()
    {
        RoomFits = LoadRoomIndex();
    }

    // Local population seed derivation. HashCode.Combine has a process-random salt and cannot reproduce instances.
    public static int PopulationSeed(ushort areaHash, uint areaSeed) => unchecked((int)InstanceEntities.ObjectTypeHash(
        FormattableString.Invariant($"population:{areaHash:x4}:{areaSeed:x8}")));

    /// <summary>How many of the shared map pool's packs (<see cref="PackCatalog.AreaPacks.Pool"/>) one map instance
    /// draws. How many pack types an instance has is decided by the official server and is not in the client, so this
    /// is our own number like <see cref="CellsPerPack"/>, chosen from the captured official instances' counts (see
    /// 14-monster-packs.md). Without a cap the pool would be
    /// re-rolled at every pack centre and a map would show nearly 300 varieties.</summary>
    public const int MapPoolPacks = 7;

    /// <summary>The seed the shared map pool is drawn with, from the instance's <see cref="PopulationSeed"/>: a stream
    /// of its own, so the draw does not depend on how many numbers the rest of <see cref="Plan"/> has taken and every
    /// caller of <see cref="ForInstance"/> gets the same packs.</summary>
    public static int PoolSeed(int populationSeed) => unchecked((int)InstanceEntities.ObjectTypeHash(
        FormattableString.Invariant($"mappool:{populationSeed:x8}")));

    /// <summary>This instance's packs: the area's own scattered packs (a map's Maps.dat signature packs) plus this
    /// instance's draw of <see cref="MapPoolPacks"/> packs from the shared map pool
    /// (<see cref="PackCatalog.AreaPacks.Pool"/>), by their weights, off <see cref="PoolSeed"/>. Everything that picks
    /// a monster out of "the area's packs" picks from this one set: the scattered population (<see cref="Plan"/>), a
    /// strongbox's guards and summons, an Essence's imprisoned monster and the like. Reading
    /// <see cref="PackCatalog.AreaPacks.Scattered"/> of the catalog's answer alone gives one variety on a map whose
    /// signature list is one single-entry pack. <paramref name="spawnable"/> leaves out a pool pack with no variety
    /// this server can write; pass the same predicate everywhere or the draws differ. The answer's Pool is empty, so
    /// calling this on an answer changes nothing. A pure function of (area, populationSeed).</summary>
    public static PackCatalog.AreaPacks ForInstance(PackCatalog.AreaPacks area, int populationSeed,
        Func<int, bool>? spawnable = null, List<string>? notes = null)
    {
        if (area.Pool.Count == 0) return area;
        var rng = new Random(PoolSeed(populationSeed));
        var pool = area.Pool.Where(s => spawnable is null || s.Pack.Entries.Any(e => spawnable(e.Variety))).ToList();
        var drawn = new List<(PackCatalog.Pack Pack, int Weight)>();
        while (drawn.Count < MapPoolPacks && pool.Count > 0)
        {
            var pick = PickPack(pool, rng);
            int at = pool.FindIndex(s => s.Pack.Row == pick.Row);
            drawn.Add(pool[at]);
            pool.RemoveAt(at);
        }
        notes?.Add($"map pool: {area.Pool.Count} pack(s) suit {area.AreaId}, this instance drew "
            + string.Join(", ", drawn.Select(p => p.Pack.Id)));
        return area with { Scattered = [.. area.Scattered, .. drawn], Pool = [] };
    }

    public sealed record Spawn(int Variety, int X, int Y, MonsterRarity Rarity, string Pack)
    {
        /// <summary>Per-area pack group id (&gt; 0 for grouped pack members, 0 for ungrouped/single spawns), for pack-wide aggro and emerge.</summary>
        public int PackId { get; init; }
    }

    public sealed record AdditionalPacks(string Source, IReadOnlyList<PackCatalog.Pack> Packs, int Count, double Size = 1);

    /// <summary>Pack members around explicit encounter centres, using the ordinary member placement and count rules.</summary>
    public static List<Spawn> AtPacks(IReadOnlyList<(PackCatalog.Pack Pack, int X, int Y, MonsterRarity Rarity)> packs,
        Func<int, int, bool> walkable, Func<int, bool> spawnable, int seed, double size = 1,
        IEnumerable<(int X, int Y)>? occupied = null)
    {
        var result = new List<Spawn>();
        var taken = occupied is null ? [] : occupied.ToHashSet();
        var random = new Random(seed);
        int nextPackId = 1;
        foreach (var p in packs)
            AddPack(result, taken, walkable, (p.X, p.Y), p.Pack, p.Rarity, true, random, spawnable, packSize: size, packId: nextPackId++);
        return result;
    }

    /// <summary>A named point of a room the client placed (found by <see cref="LoadPoints"/>).</summary>
    public sealed record RoomPoint(string Name, int X, int Y, float Angle = 0)
    {
        /// <summary>Source .arm room path when known (used to prefer dedicated boss rooms over golden_chest.arm).</summary>
        public string? Room { get; init; }
    }

    /// <summary>A server object of a room the client placed (an NPC, a quest blocker): its type and model, grid cell,
    /// angle (radians) and scale, through the room's placement.</summary>
    /// <summary>A room's server object in grid cells; <paramref name="Height"/> is its line's fixed height (world z, down),
    /// null to stand on the terrain; <paramref name="Mirrored"/> whether its room is placed mirrored (orientation codes 4–7).</summary>
    public sealed record RoomObject(string Type, string Model, int X, int Y, float Angle, float Scale, float? Height = null, bool Mirrored = false);

    /// <summary>A quest room placed where the client placed it (<see cref="FitRooms"/>): its points and server objects
    /// in grid cells (a Trial of Ascendancy's room: its traps, its pack points and monsters).</summary>
    public sealed record PlacedRoom(string Path, IReadOnlyList<RoomPoint> Points, IReadOnlyList<RoomObject> Objects);

    /// <summary>The rarity of the pack a room's pack point places ("normal", "magic", "rare"); null for any other point.</summary>
    public static MonsterRarity? PackPoint(string name) => name switch
    {
        "normal" => MonsterRarity.Normal, "magic" => MonsterRarity.Magic, "rare" => MonsterRarity.Rare, _ => null,
    };

    /// <summary>The pack points of a generated layout's placed rooms, in grid cells, per room and once each (a room is in
    /// every record that holds it). Placed as AreaObjects.RoomObjects places a room's lines.</summary>
    public static List<PlacedRoom> PackRooms(IEnumerable<GameServer.Data.Terrain.Generation.DoodadRecord> records)
    {
        var rooms = new List<PlacedRoom>();
        var seen = new HashSet<(string, int, int, int)>();
        foreach (var record in records)
            foreach (var room in record.Rooms)
            {
                if (room.Orientation is < 0 or > 7 || !room.Room.Points.Any(p => PackPoint(p.Name) is not null)) continue;
                if (!seen.Add((room.Room.Path.ToLowerInvariant(), room.X, room.Y, room.Orientation))) continue;
                rooms.Add(new PlacedRoom(room.Room.Path, [.. room.Room.Points.Where(p => PackPoint(p.Name) is not null).Select(p =>
                {
                    var (x, y) = GameServer.Data.Terrain.Generation.Orientation.Map(room.Orientation, (int)p.X, (int)p.Y,
                        room.Room.Width * 23, room.Room.Height * 23);
                    return new RoomPoint(p.Name, room.X * 23 + x, room.Y * 23 + y);
                })], []));
            }
        return rooms;
    }

    static bool IsMonsterPoint(string name) => BossPoint(name) is not null || name.StartsWith("%area%-", StringComparison.Ordinal);

    /// <summary>The bosses a fight brings rather than the area: a later Bosses row with the name of an earlier one whose
    /// Id is that row's Id + "2" (Prisoner's Gate's Abberath2, The Oasis's Shakari2, The Harvest's MalachaiBoss2: the
    /// second form appears when the first flees or falls), and <see cref="FormedLater"/>. Bosses that only share a name
    /// are separate bosses (The Mud Flats' three Forgotten Warriors KaomArcherMinion1-3, The Grain Gate's Gemling
    /// Legionnaires). They are marked
    /// <see cref="PackCatalog.AreaPacks.Absent"/>: a quest or the fight creates them.</summary>
    public static PackCatalog.AreaPacks LaterForms(PackCatalog.AreaPacks area, Func<int, (string Id, string Name)?> variety)
    {
        var absent = new HashSet<int>(area.Absent);
        for (int i = 0; i < area.Bosses.Count; i++)
        {
            if (variety(area.Bosses[i]) is not { } v) continue;
            bool secondForm = Enumerable.Range(0, i).Any(j => variety(area.Bosses[j]) is { } first && first.Name == v.Name && v.Id == first.Id + "2");
            if (secondForm || FormedLater.Contains(v.Id)) absent.Add(i);
        }
        return absent.Count == area.Absent.Count ? area : area with { Absent = absent };
    }

    /// <summary>Later forms under a name of their own: The Rotting Core's Depraved Trinity forms in the Black Heart once
    /// Doedre, Darksoul, Maligaro, The Broken and Shavronne, Unbound (its mapboss2-4 rooms) are dead.</summary>
    static readonly HashSet<string> FormedLater = new(StringComparer.Ordinal) { "Metadata/Monsters/UnholyTrio/UnholyTrio" };

    /// <summary>A unique boss point and the WorldAreas Bosses_MonsterVarietiesKeys places (1-based) it takes; empty for a
    /// point that takes the area's bosses no other point took; null for any other name. The room files write
    /// "mapboss&lt;n&gt;" (The Upper Prison: mapboss1 the Necromancer, mapboss2 Brutus; The Grain Gate: mapboss1 … mapboss7,
    /// the seven Gemling Legionnaires), "mapboss&lt;n&gt;&amp;&lt;m&gt;" (two bosses at one point, maps), a "WG" suffix
    /// (Prisoner's Gate's bossphase1/2 rooms: mapboss1WG Abberath, mapboss2WG his second form; The Oasis: mapboss1WG
    /// Shakari), and for "any": "mapboss", "force:mapboss" (Kitava's arenas, The Cathedral Rooftop and The Feeding
    /// Trough), "mapbosspack" (The Toxic Conduits: the five Lingering Aberrations) and "mapbossWG". Not taken:
    /// "mapboss-&lt;variety&gt;" (Shaper guardian maps) and the league points "mapbosswarband…", "mapbossexilepack".</summary>
    public static IReadOnlyList<int>? BossPoint(string name)
    {
        var m = BossPointName.Match(name);
        if (!m.Success) return null;
        return m.Groups[1].Success ? [.. m.Groups[1].Value.Split('&').Select(int.Parse)] : [];
    }

    /// <summary>Server-side spawner objects of a room that stand for one of the area's unique bosses (their .ot names no
    /// monster: the official server turns them into it), and that boss's MonsterVarieties Id. The Quay's boss.arm:
    /// TolmanBossSpawner, Tolman in the stocks beside Clarissa, who breaks out (its "spawning" states 1 … 7).</summary>
    public static readonly IReadOnlyDictionary<string, string> BossSpawners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Metadata/Monsters/Zombies/Tolman/TolmanBossSpawner"] = "Metadata/Monsters/Zombies/ZombieBossTolman_",
        ["Metadata/Monsters/Statue/MaleLarge/Spawner_StoneStatueLargeMaleMapBoss"] = "Metadata/Monsters/Statue/StoneStatueLargeBossMale",
        ["Metadata/Monsters/Statue/MaleLarge/Spawner_StoneStatueLargeMaleBoss"] = "Metadata/Monsters/Statue/StoneStatueLargeBossMale",
    };

    /// <summary>The boss points of the area's <see cref="BossSpawners"/> among a placed room's server objects:
    /// "mapboss&lt;n&gt;" for the n-th of WorldAreas Bosses, at the spawner's cell.</summary>
    public static IEnumerable<RoomPoint> SpawnerPoints(PackCatalog.AreaPacks area, IEnumerable<RoomObject> objects, Func<int, string?> varietyId)
    {
        foreach (var o in objects)
            if (BossSpawners.TryGetValue(o.Type, out var boss)
                && Enumerable.Range(0, area.Bosses.Count).FirstOrDefault(i => varietyId(area.Bosses[i]) == boss, -1) is int row and >= 0)
                yield return new RoomPoint($"mapboss{row + 1}", o.X, o.Y, o.Angle);
    }

    /// <summary>The boss points of the area's <see cref="BossSpawners"/> among tile-placed objects (e.g. Marketplace's
    /// Marceus statue spawner).</summary>
    public static IEnumerable<RoomPoint> SpawnerPoints(PackCatalog.AreaPacks area, IEnumerable<GameServer.Data.Terrain.Generation.PlacedTileObject> objects, Func<int, string?> varietyId)
    {
        foreach (var o in objects)
            if (BossSpawners.TryGetValue(o.Type, out var boss)
                && Enumerable.Range(0, area.Bosses.Count).FirstOrDefault(i => varietyId(area.Bosses[i]) == boss, -1) is int row and >= 0)
                yield return new RoomPoint($"mapboss{row + 1}", o.X, o.Y, o.Rotation);
    }

    /// <summary>Monster points (mapboss*, %area%-*) and pre-placed Metadata/Monsters/* points directly from a generated
    /// level's placed .arm rooms, catching rooms not indexed in room_point_areas (e.g. Imperial Gardens open_*_boss.arm).</summary>
    public static (List<RoomPoint> MonsterPoints, List<(string Room, RoomPoint Point)> DirectMonsters) LayoutRoomPoints(
        IEnumerable<GameServer.Data.Terrain.Generation.DoodadRecord> records)
    {
        var monsterPoints = new List<RoomPoint>();
        var direct = new List<(string Room, RoomPoint Point)>();
        var seenRooms = new HashSet<(string, int, int, int)>();
        foreach (var record in records)
            foreach (var room in record.Rooms)
            {
                if (room.Orientation is < 0 or > 7) continue;
                string path = room.Room.Path;
                if (!seenRooms.Add((path.ToLowerInvariant(), room.X, room.Y, room.Orientation))) continue;
                foreach (var p in room.Room.Points)
                {
                    if (string.IsNullOrEmpty(p.Name)) continue;
                    bool isBossOrNamed = IsMonsterPoint(p.Name);
                    bool isDirect = !isBossOrNamed && p.Name.StartsWith("Metadata/Monsters/", StringComparison.Ordinal);
                    if (!isBossOrNamed && !isDirect) continue;
                    var (px, py, angle) = GameServer.Data.Terrain.Generation.AreaObjects.Place(room, p.X, p.Y, p.Angle);
                    var placed = new RoomPoint(p.Name, px, py, angle) { Room = path };
                    if (isBossOrNamed) monsterPoints.Add(placed);
                    else direct.Add((path, placed));
                }
            }
        return (monsterPoints, direct);
    }

    static readonly System.Text.RegularExpressions.Regex BossPointName =new("^(?:force:)?mapboss(?:([1-9][0-9]*(?:&[1-9][0-9]*)*)|pack)?(?:WG)?$");

    // One scattered pack per this many walkable cells (The Coast seed 1: 404 620 cells → 45 packs).
    public const int CellsPerPack = 9000;
    // Percent of scattered packs that are magic (every member), and that have one rare member (the rest stay normal).
    public const int MagicChance = 12, RareChance = 6;
    // At least this many packs with a rare in an area that has scattered packs.
    public const int MinRares = 2;
    // Provisional reconstructed opening capture: 43 scattered monsters, Hillock and the tutorial zombie.
    // The first 35 seconds were not recorded; this is a calibration target, not a recovered server constant.
    public const int StrandMonsterTarget = 45;
    // Cells kept free around the player's arrival point, between pack centres, and around area transitions/waypoints
    // (~5 tiles: off-screen and outside standard 105-cell monster aggro range, capped on small arenas/tests).
    const int SpawnClearance = 120, PackSpacing = 28;
    /// <summary>Cells kept clear around an area transition or waypoint for scattered/named packs and shrine guards.</summary>
    public const int TransitionClearance = 120;
    // A pack centre needs this many walkable cells to the nearest blocked cell; members stand within MemberRadius of it.
    const int CentreDepth = 5, MemberRadius = 9, MemberSpacing = 3;

    /// <summary>How far (cells) a shrine's guards may stand from it: outside the shrine's object size (the statue and its
    /// base) and a little wider than an ordinary pack, so a pack of four still fits around the shrine.</summary>
    public const int ShrineGuardRadius = 12;

    /// <summary>A map's pack size and extra packs (MapMods): Size multiplies members per pack.</summary>
    public readonly record struct PackScale(double Size = 1, int ExtraNormal = 0, int ExtraMagic = 0, int ExtraRare = 0,
        int RareIncrease = 0, int MagicIncrease = 0);

    /// <param name="roomPacks">Points of rooms whose "normal", "magic" and "rare" points place one of the area's scattered
    /// packs of that rarity (a Trial of Ascendancy's rooms: the Lower Prison trial's plaque platform).</param>
    /// <param name="scatterable">Where a scattered pack may have its centre, besides the walk map (not among a trial's
    /// traps); null: anywhere.</param>
    // Keep the earlier CLR signatures for independently compiled modules and mods.
    public static List<Spawn> Plan(PackCatalog.AreaPacks area, int w, int h, Func<int, int, bool> walkable, (int X, int Y) arrival,
        IReadOnlyList<(int X, int Y)> transitions, IReadOnlyList<RoomPoint> points, int seed, Func<int, bool> spawnable, List<string> notes,
        PackScale scale, bool unassignedAtArrival, IReadOnlyList<RoomPoint>? roomPacks,
        Func<int, int, bool>? scatterable,
        IReadOnlyList<(string Shrine, ShrineGuards.Rule Rule, IReadOnlyList<PackCatalog.Pack> Packs, int X, int Y)>? shrineGuards,
        Func<int, bool>? guardSpawnable) =>
        Plan(area, w, h, walkable, arrival, transitions, points, seed, spawnable, notes, scale, unassignedAtArrival,
            roomPacks, scatterable, shrineGuards, guardSpawnable, null, true);

    public static List<Spawn> Plan(PackCatalog.AreaPacks area, int w, int h, Func<int, int, bool> walkable, (int X, int Y) arrival,
        IReadOnlyList<(int X, int Y)> transitions, IReadOnlyList<RoomPoint> points, int seed, Func<int, bool> spawnable, List<string> notes,
        PackScale scale, bool unassignedAtArrival, IReadOnlyList<RoomPoint>? roomPacks,
        Func<int, int, bool>? scatterable,
        IReadOnlyList<(string Shrine, ShrineGuards.Rule Rule, IReadOnlyList<PackCatalog.Pack> Packs, int X, int Y)>? shrineGuards,
        Func<int, bool>? guardSpawnable, IReadOnlyList<AdditionalPacks>? additionalPacks) =>
        Plan(area, w, h, walkable, arrival, transitions, points, seed, spawnable, notes, scale, unassignedAtArrival,
            roomPacks, scatterable, shrineGuards, guardSpawnable, additionalPacks, true);

    public static List<Spawn> Plan(PackCatalog.AreaPacks area, int w, int h, Func<int, int, bool> walkable, (int X, int Y) arrival,
        IReadOnlyList<(int X, int Y)> transitions, IReadOnlyList<RoomPoint> points, int seed, Func<int, bool> spawnable, List<string> notes,
        PackScale scale = default, bool unassignedAtArrival = false, IReadOnlyList<RoomPoint>? roomPacks = null,
        Func<int, int, bool>? scatterable = null,
        IReadOnlyList<(string Shrine, ShrineGuards.Rule Rule, IReadOnlyList<PackCatalog.Pack> Packs, int X, int Y)>? shrineGuards = null,
        Func<int, bool>? guardSpawnable = null, IReadOnlyList<AdditionalPacks>? additionalPacks = null, bool ordinaryPacks = true,
        IReadOnlyList<((int X, int Y) From, (int X, int Y) To)>? localTransitions = null) =>
        Plan(area, w, h, walkable, arrival, transitions, points, seed, spawnable, notes, scale, unassignedAtArrival,
            roomPacks, scatterable, shrineGuards, guardSpawnable, additionalPacks, ordinaryPacks, localTransitions, null);

    public static List<Spawn> Plan(PackCatalog.AreaPacks area, int w, int h, Func<int, int, bool> walkable, (int X, int Y) arrival,
        IReadOnlyList<(int X, int Y)> transitions, IReadOnlyList<RoomPoint> points, int seed, Func<int, bool> spawnable, List<string> notes,
        PackScale scale, bool unassignedAtArrival, IReadOnlyList<RoomPoint>? roomPacks,
        Func<int, int, bool>? scatterable,
        IReadOnlyList<(string Shrine, ShrineGuards.Rule Rule, IReadOnlyList<PackCatalog.Pack> Packs, int X, int Y)>? shrineGuards,
        Func<int, bool>? guardSpawnable, IReadOnlyList<AdditionalPacks>? additionalPacks, bool ordinaryPacks,
        IReadOnlyList<((int X, int Y) From, (int X, int Y) To)>? localTransitions,
        GameServer.Data.Terrain.Generation.TileGrounds? grounds)
    {
        // A map area's own draw from the shared pool, when the caller has not drawn it already (then Pool is empty and
        // this changes nothing).
        area = ForInstance(area, seed, spawnable, notes);
        var rng = new Random(seed);
        var spawns = new List<Spawn>();
        var taken = new HashSet<(int, int)>();
        var centres = new SpatialPoints(PackSpacing);
        int nextPackId = 1;
        int spawnClearance = Math.Min(SpawnClearance, Math.Max(25, Math.Min(w, h) / 4));
        int transitionClearance = Math.Min(TransitionClearance, Math.Max(25, Math.Min(w, h) / 4));
        int memberSpawnClearance = Math.Max(0, spawnClearance - MemberRadius);
        int memberTransitionClearance = Math.Max(0, transitionClearance - MemberRadius);
        bool InSafeZone((int X, int Y) p, int sc, int tc) => Near(p, arrival, sc) || transitions.Any(t => Near(p, t, tc));
        bool GroundAllows(PackCatalog.Pack pack, (int X, int Y) c)
        {
            if (grounds is null) return true;
            if (pack.ExcludedGrounds.Count > 0 && grounds.CellExcluded(c.X, c.Y, pack.ExcludedGrounds)) return false;
            if (pack.RequiredGrounds.Count > 0 && !grounds.CellMatchesAny(c.X, c.Y, pack.RequiredGrounds)) return false;
            return true;
        }

        // The room points first. Each boss of WorldAreas Bosses stands once: at its own numbered point ("mapboss2"), in a
        // named pack whose boss it is (The Mines Level 1's "%area%-minerhammerbosspack": Pikerivet), or else, with every
        // other boss left, at one "mapboss" point (The Den: The Great White Bones with The Bone Sculptor; The Solaris
        // Temple Level 1: both seals). Bosses the instance leaves out (area.Absent: later forms, The Ashen Fields' map boss override) stand nowhere. The area's
        // boss pack stands once: at its own "%area%-bosspack" points when the layout has them, else at the first boss point
        // (before, every boss point added one: The Cavern of Anger, The Dried Lake and The Lunaris Temple Level 2 had it
        // up to three times).
        var placedRows = new HashSet<int>(area.Absent);
        var placedNamedPacks = new HashSet<string>(StringComparer.Ordinal);
        bool guarded = points.Any(p => p.Name == "%area%-bosspack");
        void Bosses((int X, int Y) at, string point, IReadOnlyList<int> rows)
        {
            int bossPackId = nextPackId++;
            for (int i = 0; i < rows.Count; i++)
            {
                int variety = area.Bosses[rows[i]];
                if (!spawnable(variety)) { notes.Add($"room point {point} at {at}: boss {variety} cannot be created"); continue; }
                if (AddMember(spawns, taken, walkable, at, variety, MonsterRarity.Unique, point, rng, i == 0 ? 0 : 3, packId: bossPackId)) continue;
                // Some bosses' points are off the walk map (Kitava in his pit, Arakaali in her web, Doedre the Vile at her
                // cauldron): the nearest free walkable cell, so the server's walk and AI have a cell to start from.
                if (Nearest(at, walkable, taken, BossSearchRadius) is { } cell)
                {
                    taken.Add(cell);
                    spawns.Add(new Spawn(variety, cell.X, cell.Y, MonsterRarity.Unique, point) { PackId = bossPackId });
                    notes.Add($"room point {point} at {at} is not walkable: boss {variety} at {cell}");
                }
                else notes.Add($"room point {point} at {at}: no free walkable cell within {BossSearchRadius} for boss {variety}");
            }
            if (!guarded && area.Named.TryGetValue("bosspack", out var guards))
            {
                AddPack(spawns, taken, walkable, at, guards, MonsterRarity.Normal, false, rng, spawnable, packId: bossPackId);
                placedNamedPacks.Add("bosspack");
                guarded = true;
            }
            centres.Add(at);
        }
        var bossPoints = points.Select(p => (Point: p, Rows: BossPoint(p.Name))).Where(b => b.Rows is not null).ToList();
        foreach (var (p, rows) in bossPoints.Where(b => b.Rows!.Count > 0))
        {
            var mine = rows!.Select(n => n - 1).Where(i => i < area.Bosses.Count && placedRows.Add(i)).ToList();
            if (mine.Count == 0)
            {
                notes.Add($"room point {p.Name} at ({p.X}, {p.Y}): {(rows!.Any(n => n > area.Bosses.Count) ? $"no such unique boss for {area.AreaId}" : "its boss stands elsewhere")}");
                continue;
            }
            Bosses((p.X, p.Y), p.Name, mine);
        }
        foreach (var p in points.Where(p => p.Name.StartsWith("%area%-", StringComparison.Ordinal)))
        {
            string name = p.Name["%area%-".Length..];
            if (!area.Named.TryGetValue(name, out var pack)) { notes.Add($"room point {p.Name} at ({p.X}, {p.Y}): no pack {area.AreaId}-{name}"); continue; }
            bool bossNamed = name == "bosspack" || pack.UniqueBossPack || pack.BossVarieties.Any(area.Bosses.Contains);
            if (!ordinaryPacks && !bossNamed) continue;
            if (!bossNamed && InSafeZone((p.X, p.Y), spawnClearance, transitionClearance)) continue;
            // A named pack's own boss (BossMonster_MonsterVarietiesKeys) comes with it; one of the area's unique bosses
            // or a MonsterPacks.__col8 mini-boss pack is unique, and an area boss stands only once.
            int Row(int variety) => Enumerable.Range(0, area.Bosses.Count).FirstOrDefault(i => area.Bosses[i] == variety && !placedRows.Contains(i), -1);
            bool IsBoss(int variety) => area.Bosses.Contains(variety);
            Func<int, int, bool> namedWalkable = bossNamed ? walkable
                : (x, y) => walkable(x, y) && !InSafeZone((x, y), memberSpawnClearance, memberTransitionClearance);
            foreach (int b in AddPack(spawns, taken, namedWalkable, (p.X, p.Y), pack, MonsterRarity.Normal, true, rng, spawnable,
                b => !IsBoss(b) ? pack.UniqueBossPack : Row(b) >= 0 ? true : (bool?)null, packId: nextPackId++))
                if (Row(b) is int r and >= 0) placedRows.Add(r);
            placedNamedPacks.Add(name);
            centres.Add((p.X, p.Y));
        }
        var rest = Enumerable.Range(0, area.Bosses.Count).Where(i => !placedRows.Contains(i)).ToList();
        var any = bossPoints.Where(b => b.Rows!.Count == 0).Select(b => b.Point).ToList();
        if (any.Count > 0 && rest.Count > 0)
        {
            // Prefer dedicated boss rooms over treasure alcoves (golden_chest.arm carries a generic mapboss point).
            var dedicated = any.Where(pt => pt.Room is null || !pt.Room.EndsWith("/golden_chest.arm", StringComparison.OrdinalIgnoreCase)).ToList();
            var pool = dedicated.Count > 0 ? dedicated : any;
            var p = pool[rng.Next(pool.Count)];
            Bosses((p.X, p.Y), p.Name, rest);
        }
        else if (rest.Count > 0 && unassignedAtArrival)
        {
            notes.Add($"no room point for unique boss(es) {string.Join(", ", rest.Select(i => area.Bosses[i]))} of {area.AreaId}: standing at arrival");
            for (int i = 0; i < rest.Count; i++)
                Bosses((arrival.X + (i + 1) * 4, arrival.Y), "mapboss", [rest[i]]);
        }
        else if (rest.Count > 0 && LabyrinthTrials.InLabyrinth(area.AreaId))
            // The Labyrinth's areas (WorldAreas IsLabyrinthArea) list Izaro's varieties as bosses, but his fight raises him
            // itself: no fallback Izaro stands deep in the area.
            notes.Add($"no room point for unique boss(es) {string.Join(", ", rest.Select(i => area.Bosses[i]))} of {area.AreaId}: a Labyrinth area, left to its fight");
        else if (rest.Count > 0)
        {
            // The layout gave no free boss point at all (a sparse boss room the doodad fit misses on a seed nobody
            // recorded, or points whose boss already stands elsewhere): the area's unique boss still stands, deep in the
            // area. Before, it stood nowhere and the area had no boss, which is why a new area needed a walk map visit for
            // its boss.
            var deep = Farthest(w, h, walkable, arrival, transitions);
            notes.Add($"no room point for unique boss(es) {string.Join(", ", rest.Select(i => area.Bosses[i]))} of {area.AreaId}: standing at ({deep.X}, {deep.Y})");
            for (int i = 0; i < rest.Count; i++)
                Bosses((deep.X + (i + 1) * 4, deep.Y), "mapboss", [rest[i]]);
        }

        // The scattered packs: random deep cells away from the arrival, the area transitions and other packs.
        var packs = area.Scattered.Where(s => s.Pack.Entries.Any(e => spawnable(e.Variety))).ToList();
        // Shrine guards (ShrineGuards): the pack rows the shrine's Shrines row names when it names any, else one of the
        // area's own packs at that pack's own count. One pack a shrine, normal rarity unless the rule says otherwise.
        // A guard has to be seen: an emerge variety (Act1-ZombieMiredEmerge, Act1-SandspitterEmerge) starts buried and
        // only rises when the player is on top of it, which leaves the shrine looking unguarded. The area's standing
        // varieties are preferred; a pack with none is still used rather than leaving the shrine bare.
        var visible = guardSpawnable ?? spawnable;
        bool PackStands(PackCatalog.Pack p) => p.Entries.Any(e => visible(e.Variety));
        foreach (var (shrine, rule, own, x, y) in shrineGuards ?? [])
        {
            if (!rule.Required) continue;
            var at = (X: x, Y: y);
            PackCatalog.Pack? guard;
            if (rule.AreaPool)
            {
                var standing = packs.Where(p => PackStands(p.Pack)).ToList();
                guard = standing.Count > 0 ? PickPack(standing, rng) : packs.Count > 0 ? PickPack(packs, rng) : null;
            }
            else
            {
                var standing = own.Where(PackStands).ToList();
                guard = standing.Count > 0 ? standing[rng.Next(standing.Count)] : own.Count > 0 ? own[rng.Next(own.Count)] : null;
            }
            if (guard is null)
            {
                notes.Add($"shrine {shrine} at {at}: no pack of {area.AreaId} to guard it");
                continue;
            }
            // Around the shrine, never on it: the statue and its base stay clear by the shrine type's object size, and
            // the pack rings it a little further out than an ordinary pack's members stand.
            int first = spawns.Count;
            AddPack(spawns, taken, walkable, at, guard, rule.Rarity, true, rng, PackStands(guard) ? visible : spawnable, memberLimit: rule.Fixed ?? int.MaxValue,
                keepClear: ShrinePlacement.ObjectSize, maxRadius: ShrineGuardRadius, ring: true, packId: nextPackId++);
            centres.Add(at);
            var mine = spawns.Skip(first).ToList();
            notes.Add($"shrine {shrine} at {at}: guarded by {guard.Id} " +
                (rule.Fixed is int fixedCount ? $"({fixedCount})" : $"({guard.MinCount}\u2013{guard.MaxCount})") +
                (mine.Count == 0
                    ? ": no cell free around it"
                    : $": {mine.Count} around it at " + string.Join(" ", mine.Select(s =>
                        $"({s.X - at.X},{s.Y - at.Y})"))));
        }
        if (packs.Count == 0 && additionalPacks is not { Count: > 0 }) return spawns;
        // The layout's own pack points stand their packs first, where the room puts them (issue #668).
        int roomPlaced = 0;
        foreach (var p in packs.Count == 0 || !ordinaryPacks ? [] : roomPacks ?? [])
            if (PackPoint(p.Name) is { } rarity)
            {
                if (InSafeZone((p.X, p.Y), spawnClearance, transitionClearance)) continue;
                var groundMatching = grounds is null ? packs : packs.Where(s => GroundAllows(s.Pack, (p.X, p.Y))).ToList();
                var pack = PickPack(groundMatching.Count > 0 ? groundMatching : packs, rng);
                int before = spawns.Count;
                AddPack(spawns, taken, (x, y) => walkable(x, y) && !InSafeZone((x, y), memberSpawnClearance, memberTransitionClearance),
                    (p.X, p.Y), pack, rarity, true, rng, spawnable, packId: nextPackId++);
                if (spawns.Count > before) roomPlaced++;
                centres.Add((p.X, p.Y));
                notes.Add($"room point {p.Name} at ({p.X}, {p.Y}): {pack.Id}");
            }
        // One pass over the grid serves the depth flood, the reach flood and every member's cell: the delegate was asked
        // up to eight times per cell, over 3.2 million cells on a generated map.
        var cells = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                cells[y * w + x] = walkable(x, y);
        var depth = Depth(w, h, cells);
        // A pack in a pocket the arrival cannot walk to is a monster the area's count keeps and nobody can kill: the
        // map reads "more than 50 monsters remaining" long after it was cleared. The walk map of a generated map has
        // such pockets (cells walled off from the entrance), so the packs stand only where the player can reach them.
        var reach = Reachable(w, h, cells, arrival, localTransitions);
        var candidates = new List<(int X, int Y)>();
        int pocket = 0;
        for (int y = 0; y < h; y += 3)
            for (int x = 0; x < w; x += 3)
                if (depth[y * w + x] >= CentreDepth)
                {
                    if (reach is not null && !reach[y * w + x]) pocket++;
                    else candidates.Add((x, y));
                }
        int walkableCells = depth.Count(d => d > 0);
        int packCells = reach is null ? walkableCells : reach.Count(r => r);
        if (pocket > 0)
            notes.Add($"{pocket} deep cell(s) the arrival cannot walk to stand no pack " +
                $"({walkableCells - packCells} of {walkableCells} walkable cell(s) cut off)");
        Shuffle(candidates, rng);
        bool ReachableMember(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h
            && (reach is null || reach[y * w + x]) && cells[y * w + x]
            && !InSafeZone((x, y), memberSpawnClearance, memberTransitionClearance);
        // Roaming MonsterPacks.__col8 mini-boss packs (Ungulath, The Burning Menace, Seleslatha, Targa, Soulmourn, Voidscream)
        // that have no dedicated .arm room point in this seed stand at a free reachable cell with their Unique mini-boss.
        bool strand = area.AreaId == TutorialObjects.Strand;
        if (ordinaryPacks && !strand)
        {
            foreach (var (name, miniPack) in area.Named.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!miniPack.UniqueBossPack || miniPack.BossChance <= 0 || miniPack.BossVarieties.Count == 0
                    || placedNamedPacks.Contains(name)) continue;
                // Skip if its boss variety is a WorldAreas.Bosses entry of this or a sibling area (e.g. Act 4 Mines
                // 1_4_3_1-minerhammerbosspack belongs to 1_4_3_2's Bosses, 1_4_3_2-minehandfingerbosspack to 1_4_3_3's):
                // WorldAreas bosses are placed by their own area's boss pass, not duplicated as roaming mini-bosses.
                bool isWorldAreaBoss = miniPack.BossVarieties.Any(bv => area.Bosses.Contains(bv) || area.AllWorldBosses.Contains(bv));
                if (isWorldAreaBoss || !miniPack.BossVarieties.Any(spawnable)) continue;
                (int X, int Y)? spot = null;
                foreach (var c in candidates)
                {
                    if (InSafeZone(c, spawnClearance, transitionClearance) || centres.AnyNear(c)
                        || scatterable is not null && !scatterable(c.X, c.Y) || !GroundAllows(miniPack, c)) continue;
                    spot = c;
                    break;
                }
                if (spot is null && grounds is not null)
                    foreach (var c in candidates)
                    {
                        if (InSafeZone(c, spawnClearance, transitionClearance) || centres.AnyNear(c)
                            || scatterable is not null && !scatterable(c.X, c.Y)) continue;
                        spot = c;
                        break;
                    }
                if (spot is not { } at) continue;
                foreach (int b in AddPack(spawns, taken, ReachableMember, at, miniPack, MonsterRarity.Normal, true, rng, spawnable,
                    _ => true, packId: nextPackId++))
                    for (int i = 0; i < area.Bosses.Count; i++)
                        if (area.Bosses[i] == b) placedRows.Add(i);
                placedNamedPacks.Add(name);
                centres.Add(at);
                notes.Add($"named mini-boss pack {miniPack.Id} at {at}");
            }
        }
        foreach (var request in additionalPacks ?? [])
        {
            int placed = 0, first = spawns.Count;
            var choices = request.Packs.Where(p => p.Entries.Count > 0 && p.Entries.All(e => spawnable(e.Variety))).ToList();
            if (choices.Count > 0)
                foreach (var c in candidates)
                {
                    if (placed >= request.Count) break;
                    if (InSafeZone(c, spawnClearance, transitionClearance)
                        || centres.AnyNear(c) || scatterable is not null && !scatterable(c.X, c.Y)) continue;
                    var pack = choices[rng.Next(choices.Count)];
                    int before = spawns.Count;
                    AddPack(spawns, taken, ReachableMember, c, pack, RollRarity(rng.Next(100)), true, rng, spawnable, packSize: request.Size, packId: nextPackId++);
                    if (spawns.Count == before) continue;
                    centres.Add(c);
                    placed++;
                }
            notes.Add($"additional packs {request.Source}: placed {placed}/{request.Count}, {spawns.Count - first} monsters");
        }
        if (packs.Count == 0 || !ordinaryPacks) return spawns;
        double size = scale.Size > 0 ? scale.Size : 1;
        int wanted = (int)Math.Round(packCells / (double)CellsPerPack
            * SystemSettings.Default.MonsterPackDensityMultiplier * area.Density);
        // Room packs consume only the ordinary budget. Explicit additional packs still stand even when the rooms
        // already exceed that budget.
        wanted = Math.Max(0, wanted - roomPlaced) + scale.ExtraNormal + scale.ExtraMagic + scale.ExtraRare;
        if (wanted <= 0) { notes.Add($"{roomPlaced} room pack point(s) place the area's packs"); return spawns; }
        var plans = new List<((int X, int Y) At, PackCatalog.Pack Pack)>();
        bool hasGroundFilter = grounds is not null && packs.Any(p => p.Pack.RequiredGrounds.Count > 0 || p.Pack.ExcludedGrounds.Count > 0);
        foreach (var c in candidates)
        {
            if (plans.Count >= wanted) break;
            if (InSafeZone(c, spawnClearance, transitionClearance) || centres.AnyNear(c)
                || scatterable is not null && !scatterable(c.X, c.Y)) continue;
            var eligible = hasGroundFilter ? packs.Where(p => GroundAllows(p.Pack, c)).ToList() : packs;
            if (eligible.Count == 0) continue;
            centres.Add(c);
            plans.Add((c, PickPack(eligible, rng)));
        }
        // Safe unconstrained fallback if strict ground-type filtering left the area underpopulated on a seed.
        if (hasGroundFilter && plans.Count < wanted)
            foreach (var c in candidates)
            {
                if (plans.Count >= wanted) break;
                if (InSafeZone(c, spawnClearance, transitionClearance) || centres.AnyNear(c)
                    || scatterable is not null && !scatterable(c.X, c.Y)) continue;
                centres.Add(c);
                plans.Add((c, PickPack(packs, rng)));
            }
        List<MonsterRarity> rarities;
        if (strand)
            // The Twilight Strand (1_1_1) spawns only normal scattered packs; Hillock is its sole non-normal monster.
            rarities = [.. Enumerable.Repeat(MonsterRarity.Normal, plans.Count)];
        else
        {
            // Rarity per pack: rare (one rare member), magic (all members) or normal; at least MinRares rares.
            rarities = plans.Select(_ => RollRarity(rng.Next(100))).ToList();
            for (int i = 0; rarities.Count(r => r == MonsterRarity.Rare) < Math.Min(MinRares, plans.Count) && i < 100; i++)
                rarities[rng.Next(plans.Count)] = MonsterRarity.Rare;
            Promote(rarities, MonsterRarity.Rare, scale.ExtraRare + (int)Math.Round(plans.Count * scale.RareIncrease / 100.0));
            Promote(rarities, MonsterRarity.Magic, scale.ExtraMagic + (int)Math.Round(plans.Count * scale.MagicIncrease / 100.0));
        }
        int scatteredStart = spawns.Count;
        int memberBudget = strand ? Math.Max(0, StrandMonsterTarget - 1 - scatteredStart) : int.MaxValue;
        int placedPacks = 0;
        for (int i = 0; i < plans.Count && spawns.Count - scatteredStart < memberBudget; i++)
        {
            AddPack(spawns, taken, ReachableMember, plans[i].At, plans[i].Pack, rarities[i], true, rng, spawnable, packSize: size,
                memberLimit: memberBudget - (spawns.Count - scatteredStart), packId: nextPackId++);
            placedPacks++;
        }
        notes.Add($"{placedPacks} scattered packs ({rarities.Take(placedPacks).Count(r => r == MonsterRarity.Magic)} magic, {rarities.Take(placedPacks).Count(r => r == MonsterRarity.Rare)} with a rare) " +
            $"for {walkableCells} walkable cells, {points.Count} room point(s), {spawns.Count} monsters");
        return spawns;
    }

    /// <summary>A pack around <paramref name="at"/>; returns the members that are unique bosses. With
    /// <paramref name="uniqueBoss"/>, the pack's boss monster is unique when it says true, as the pack's rarity gives
    /// when false, and left out when null (that unique boss stands elsewhere already).</summary>
    static void Promote(List<MonsterRarity> rarities, MonsterRarity rarity, int extra)
    {
        for (int i = 0; extra > 0 && i < rarities.Count; i++)
            if (rarities[i] != rarity && (rarity != MonsterRarity.Rare || rarities[i] != MonsterRarity.Unique))
            {
                rarities[i] = rarity;
                extra--;
            }
    }

    static List<int> AddPack(List<Spawn> spawns, HashSet<(int, int)> taken, Func<int, int, bool> walkable, (int X, int Y) at, PackCatalog.Pack pack,
        MonsterRarity rarity, bool withBoss, Random rng, Func<int, bool> spawnable, Func<int, bool?>? uniqueBoss = null, double packSize = 1,
        int memberLimit = int.MaxValue, int keepClear = 0, int maxRadius = MemberRadius, bool ring = false, int packId = 0)
    {
        var members = new List<(int Variety, bool Unique)>();
        // MonsterPackEntries with Flag = 0 spawn exact Weight counts (boss/encounter packs such as 1_3_3_1-bosspack,
        // 1_3_18_2-dominusportal, 1_1_11_1-bosspack, 1_5_3-TemplarElite).
        int fixedAdded = 0;
        foreach (var (variety, fixedCount) in pack.FixedEntries)
            if (fixedCount > 0 && spawnable(variety))
                for (int i = 0; i < fixedCount; i++)
                {
                    members.Add((variety, false));
                    fixedAdded++;
                }
        bool rollRandom = pack.FixedEntries.Count == 0 || (pack.RandomEntries.Count > 0 && pack.MaxCount > 0);
        if (rollRandom)
        {
            int minRoll = pack.MinCount;
            int maxRoll = Math.Max(minRoll, pack.MaxCount);
            // In mixed fixed + random packs where MinCount already includes the fixed slots (MinCount >= FixedCount),
            // subtract the fixed count so random rolls don't double-count the fixed members.
            if (fixedAdded > 0 && pack.FixedCount > 0 && minRoll >= pack.FixedCount)
            {
                minRoll = Math.Max(0, minRoll - fixedAdded);
                maxRoll = Math.Max(minRoll, maxRoll - fixedAdded);
            }
            int rolled = rng.Next(minRoll, maxRoll + 1);
            int minMembers = fixedAdded > 0 ? 0 : 1;
            int count = Math.Max(minMembers, (int)Math.Round(rolled * SystemSettings.Default.MonsterPackSizeMultiplier * (packSize > 0 ? packSize : 1)));
            for (int i = 0; i < count; i++)
            {
                int v = pack.PickVariety(rng);
                if (spawnable(v)) members.Add((v, false));
            }
        }
        // The pack's boss monster (Act1-CoastCrabs: 80% for one large Savage Crab; The Mines Level 1's
        // minerhammerbosspack: always Pikerivet; MonsterPacks.__col8 mini-boss packs: Unique rarity).
        if (withBoss && pack.BossVarieties.Count > 0 && rng.Next(100) < pack.BossChance)
            for (int i = 0; i < pack.BossCount; i++)
                if (pack.BossVarieties[rng.Next(pack.BossVarieties.Count)] is int b && spawnable(b)
                    && (uniqueBoss is null ? (bool?)pack.UniqueBossPack : uniqueBoss(b)) is bool unique && !members.Contains((b, true)))
                    members.Insert(0, (b, unique));
        var uniques = new List<int>();
        int before = spawns.Count;
        // A shrine's guards stand evenly around it (ring): one cell per member on a circle whose rotation is the
        // instance's, alternating two radii so neighbours do not touch; a blocked cell falls back to the search below.
        var ringCells = ring ? RingCells(at, members.Count, keepClear, rng) : null;
        // High pack-size maps can roll dozens of monsters in one pack; expand the pack footprint beyond MemberRadius
        // so members still fit with MemberSpacing instead of failing placement inside a 19x19 square.
        int packRadius = maxRadius == MemberRadius && members.Count > 16
            ? Math.Min(36, Math.Max(MemberRadius, (int)Math.Ceiling(Math.Sqrt(members.Count) * 2.4)))
            : maxRadius;
        for (int i = 0; i < members.Count && spawns.Count - before < memberLimit; i++)
        {
            // Magic packs are magic throughout; a rare pack has one rare (the first member) among normal ones.
            var r = members[i].Unique ? MonsterRarity.Unique : rarity == MonsterRarity.Rare ? (i == 0 ? MonsterRarity.Rare : MonsterRarity.Normal) : rarity;
            if (AddMember(spawns, taken, walkable, at, members[i].Variety, r, pack.Id, rng,
                    keepClear > 0 ? keepClear : i == 0 ? 0 : 2, keepClear, packRadius,
                    ringCells is null ? null : ringCells[i], packId) && members[i].Unique) uniques.Add(members[i].Variety);
        }
        return uniques;
    }

    /// <summary>The cells a pack rings its centre with: <paramref name="count"/> bearings evenly spaced from a random
    /// starting angle, on radii <c>keepClear + 1</c> and <c>keepClear + 3</c> in turn, so neighbour spacing grows with the
    /// ring and the pack does not bunch on one side of a shrine.</summary>
    static (int X, int Y)[] RingCells((int X, int Y) at, int count, int keepClear, Random rng)
    {
        var cells = new (int X, int Y)[Math.Max(1, count)];
        double start = rng.NextDouble() * Math.PI * 2;
        for (int i = 0; i < count; i++)
        {
            double angle = start + Math.PI * 2 * i / Math.Max(1, count);
            int radius = keepClear + 1 + i % 2 * 2;
            cells[i] = (at.X + (int)Math.Round(Math.Cos(angle) * radius), at.Y + (int)Math.Round(Math.Sin(angle) * radius));
        }
        return cells;
    }

    static bool Crowded(HashSet<(int, int)> taken, int x, int y)
    {
        for (int dy = -(MemberSpacing - 1); dy < MemberSpacing; dy++)
            for (int dx = -(MemberSpacing - 1); dx < MemberSpacing; dx++)
                if (taken.Contains((x + dx, y + dy))) return true;
        return false;
    }

    static bool AddMember(List<Spawn> spawns, HashSet<(int, int)> taken, Func<int, int, bool> walkable, (int X, int Y) at, int variety,
        MonsterRarity rarity, string pack, Random rng, int minRadius, int keepClear = 0, int maxRadius = MemberRadius,
        (int X, int Y)? preferred = null, int packId = 0)
    {
        // The ring cell first, when it is free ground: that is what makes the guards a circle around the shrine.
        if (preferred is { } p && walkable(p.X, p.Y)
            && Math.Sqrt((double)(p.X - at.X) * (p.X - at.X) + (double)(p.Y - at.Y) * (p.Y - at.Y)) >= keepClear
            && !Crowded(taken, p.X, p.Y))
        {
            taken.Add(p);
            spawns.Add(new Spawn(variety, p.X, p.Y, rarity, pack) { PackId = packId });
            return true;
        }
        for (int attempt = 0; attempt < 60; attempt++)
        {
            int radius = maxRadius <= MemberRadius
                ? Math.Min(maxRadius, minRadius + attempt / 6)
                : Math.Min(maxRadius, minRadius + (int)((long)attempt * Math.Max(0, maxRadius - minRadius) / 48));
            int x = at.X + rng.Next(-radius, radius + 1), y = at.Y + rng.Next(-radius, radius + 1);
            // A shrine's guards stand around it: no cell inside keepClear of the shrine's own cell.
            if (keepClear > 0 && Math.Max(Math.Abs(x - at.X), Math.Abs(y - at.Y)) < keepClear) continue;
            if (!walkable(x, y) || Crowded(taken, x, y)) continue;
            taken.Add((x, y));
            spawns.Add(new Spawn(variety, x, y, rarity, pack) { PackId = packId });
            return true;
        }
        return false;
    }

    // How far (cells) a unique boss whose point is off the walk map may stand from it.
    const int BossSearchRadius = 60;

    /// <summary>Where the bosses the instance leaves out (<see cref="PackCatalog.AreaPacks.Absent"/>) would stand, by
    /// variety row: their own numbered room point ("mapboss1" for The Rotting Core's Depraved Trinity), or the nearest
    /// walkable cell to it. A quest that forms one later (Quests.FormsRule) creates it there.</summary>
    public static Dictionary<int, (int X, int Y)> LaterFormPoints(PackCatalog.AreaPacks area, IReadOnlyList<RoomPoint> points,
        Func<int, int, bool> walkable)
    {
        var at = new Dictionary<int, (int X, int Y)>();
        foreach (int i in area.Absent.Order())
        {
            if (i >= area.Bosses.Count || at.ContainsKey(area.Bosses[i])) continue;
            if (points.FirstOrDefault(p => BossPoint(p.Name) is { Count: > 0 } rows && rows.Contains(i + 1)) is not { } point) continue;
            if (BossCell(point, walkable) is { } cell) at[area.Bosses[i]] = cell;
        }
        return at;
    }

    public static (int X, int Y)? BossCell(RoomPoint point, Func<int, int, bool> walkable) =>
        Nearest((point.X, point.Y), walkable, [], BossSearchRadius);

    /// <summary>The free walkable cell nearest to <paramref name="at"/> within <paramref name="radius"/> (square rings
    /// outwards, the closest of each ring by distance), or null.</summary>
    static (int X, int Y)? Nearest((int X, int Y) at, Func<int, int, bool> walkable, HashSet<(int, int)> taken, int radius)
    {
        bool Free(int x, int y) => walkable(x, y) && !Crowded(taken, x, y);
        for (int r = 0; r <= radius; r++)
        {
            (int X, int Y)? best = null;
            long bestD = long.MaxValue;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r || !Free(at.X + dx, at.Y + dy)) continue;
                    long d = (long)dx * dx + (long)dy * dy;
                    if (d < bestD) { bestD = d; best = (at.X + dx, at.Y + dy); }
                }
            if (best is not null) return best;
        }
        return null;
    }

    /// <summary>The walkable cell farthest (in steps) from the arrival, clear of the area transitions: where the layout
    /// placed no boss room, its unique boss stands deep in the area rather than nowhere.</summary>
    static (int X, int Y) Farthest(int w, int h, Func<int, int, bool> walkable, (int X, int Y) arrival,
        IReadOnlyList<(int X, int Y)> transitions)
    {
        bool Clear((int X, int Y) at) =>
            !transitions.Any(t => Math.Abs(t.X - at.X) < TransitionClearance && Math.Abs(t.Y - at.Y) < TransitionClearance);
        var dist = new Dictionary<(int X, int Y), int>(w * h / 4 + 16) { [arrival] = 0 };
        var queue = new Queue<(int X, int Y)>([arrival]);
        (int X, int Y) best = arrival;
        int bestDistance = -1;
        (int X, int Y)[] steps = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            int d = dist[at];
            if (d > bestDistance && Clear(at)) { bestDistance = d; best = at; }
            foreach (var step in steps)
            {
                var next = (at.X + step.X, at.Y + step.Y);
                if (next.Item1 < 0 || next.Item1 >= w || next.Item2 < 0 || next.Item2 >= h) continue;
                if (dist.ContainsKey(next) || !walkable(next.Item1, next.Item2)) continue;
                dist[next] = d + 1;
                queue.Enqueue(next);
            }
        }
        return best;
    }

    static MonsterRarity RollRarity(int roll)
    {
        int rare = (int)Math.Round(SystemSettings.Default.CampaignPackRareChancePct, MidpointRounding.AwayFromZero);
        int magic = (int)Math.Round(SystemSettings.Default.CampaignPackMagicChancePct, MidpointRounding.AwayFromZero);
        return roll < rare ? MonsterRarity.Rare : roll < rare + magic ? MonsterRarity.Magic : MonsterRarity.Normal;
    }

    static PackCatalog.Pack PickPack(List<(PackCatalog.Pack Pack, int Weight)> packs, Random rng)
    {
        int roll = rng.Next(packs.Sum(p => p.Weight));
        foreach (var (pack, weight) in packs) if ((roll -= weight) < 0) return pack;
        return packs[^1].Pack;
    }

    // One population event is shared by all its league callbacks. Its immutable candidate cells can
    // be reused for each placement. Each roll still uses its own taken cells and random sequence.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AreaPopulated, EncounterSites> PopulationSites = new();

    public static (int X, int Y)? EncounterSpot(AreaPopulated area, IReadOnlyList<(int X, int Y)> taken, Random rng)
    {
        var sites = PopulationSites.GetValue(area, static population =>
        {
            var layout = population.Player.Layout;
            return FindEncounterSites(layout.GridW, layout.GridH, layout.Walkable!, (layout.SpawnX, layout.SpawnY),
                [.. layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY))],
                layout.Level?.FeatureAreas);
        });
        return PickEncounterSpot(sites, taken, rng, 20);
    }

    sealed record EncounterSites(List<(int X, int Y)> Candidates, List<(int X, int Y)> NearArrival);

    /// <summary>Where a league encounter stands: a cell as deep in the walk map as a pack centre, as far from the
    /// arrival and the area transitions as the packs keep, and <paramref name="spacing"/> from every taken cell (the
    /// placed monsters, other encounters), and never inside a sub-area an ExtraTerrainFeatures row adds (the Sacred
    /// Grove beside a map, a Harvest grove's room): that room holds its own mechanic, not another league's content.
    /// With no such cell the taken cells are ignored; null when the area has no cell at all.</summary>
    public static (int X, int Y)? EncounterSpot(int w, int h, Func<int, int, bool> walkable, (int X, int Y) arrival,
        IReadOnlyList<(int X, int Y)> transitions, IReadOnlyList<(int X, int Y)> taken, Random rng, int spacing = 20,
        IReadOnlyList<(int X0, int Y0, int X1, int Y1)>? featureAreas = null)
    {
        return PickEncounterSpot(FindEncounterSites(w, h, walkable, arrival, transitions, featureAreas), taken, rng, spacing);
    }

    static EncounterSites FindEncounterSites(int w, int h, Func<int, int, bool> walkable, (int X, int Y) arrival,
        IReadOnlyList<(int X, int Y)> transitions, IReadOnlyList<(int X0, int Y0, int X1, int Y1)>? featureAreas = null)
    {
        bool InFeatureArea(int x, int y)
        {
            if (featureAreas is null) return false;
            for (int i = 0; i < featureAreas.Count; i++)
            {
                var a = featureAreas[i];
                if (x >= a.X0 && y >= a.Y0 && x < a.X1 && y < a.Y1) return true;
            }
            return false;
        }
        var cells = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                cells[y * w + x] = walkable(x, y);
        var depth = Depth(w, h, cells);
        var nearArrival = new List<(int X, int Y)>();
        if (Environment.GetEnvironmentVariable("Game_ENCOUNTERS_AT_ARRIVAL") == "1")
        {
            for (int y = Math.Max(0, arrival.Y - 40); y < Math.Min(h, arrival.Y + 40); y++)
                for (int x = Math.Max(0, arrival.X - 40); x < Math.Min(w, arrival.X + 40); x++)
                    if (depth[y * w + x] >= 2 && !InFeatureArea(x, y)) nearArrival.Add((x, y));
            nearArrival = [.. nearArrival.OrderBy(cell =>
            {
                long dx = cell.X - (arrival.X + 12), dy = cell.Y - arrival.Y;
                return dx * dx + dy * dy;
            })];
        }
        var candidates = new List<(int X, int Y)>();
        for (int y = 0; y < h; y += 3)
            for (int x = 0; x < w; x += 3)
                if (depth[y * w + x] >= CentreDepth && !Near((x, y), arrival, SpawnClearance)
                    && !transitions.Any(t => Near((x, y), t, TransitionClearance))
                    && !InFeatureArea(x, y))
                    candidates.Add((x, y));
        return new(candidates, nearArrival);
    }

    static (int X, int Y)? PickEncounterSpot(EncounterSites sites, IReadOnlyList<(int X, int Y)> taken, Random rng, int spacing)
    {
        foreach (var cell in sites.NearArrival)
            if (!taken.Contains(cell)) return cell;
        if (sites.Candidates.Count == 0) return null;
        var candidates = new List<(int X, int Y)>(sites.Candidates);
        Shuffle(candidates, rng);
        var takenGrid = new SpatialPoints(spacing);
        foreach (var t in taken) takenGrid.Add(t);
        foreach (var c in candidates)
            if (!takenGrid.AnyNear(c)) return c;
        return candidates[0];
    }

    static bool Near((int X, int Y) a, (int X, int Y) b, int d) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y) < (long)d * d;

    sealed class SpatialPoints(int spacing)
    {
        readonly int _cell = Math.Max(1, spacing);
        readonly Dictionary<(int X, int Y), List<(int X, int Y)>> _buckets = new();

        (int X, int Y) Bucket((int X, int Y) p) =>
            (p.X >= 0 ? p.X / _cell : (p.X - _cell + 1) / _cell,
             p.Y >= 0 ? p.Y / _cell : (p.Y - _cell + 1) / _cell);

        public void Add((int X, int Y) p)
        {
            var key = Bucket(p);
            if (!_buckets.TryGetValue(key, out var list)) _buckets[key] = list = [];
            list.Add(p);
        }

        public bool AnyNear((int X, int Y) p)
        {
            if (spacing <= 0) return false;
            var (bx, by) = Bucket(p);
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (_buckets.TryGetValue((bx + dx, by + dy), out var list))
                        foreach (var o in list)
                            if (Near(p, o, spacing)) return true;
            return false;
        }
    }

    static void Shuffle<T>(List<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
    }

    /// <summary>Per cell: 0 when blocked, else the 4-neighbour distance to the nearest blocked cell.</summary>
    static int[] Depth(int w, int h, bool[] walkable)
    {
        var dist = new int[w * h];
        var q = new Queue<int>();
        for (int i = 0; i < dist.Length; i++)
            if (walkable[i]) dist[i] = int.MaxValue; else q.Enqueue(i);
        // The four neighbours written out: a per-cell array here allocated once per cell of the grid (3.2 million of
        // them on a generated map) and was most of the pack plan's 368 ms (live 2026-11-17).
        while (q.Count > 0)
        {
            int i = q.Dequeue(), x = i % w, y = i / w, next = dist[i] + 1;
            if (x > 0)
            {
                int j = i - 1;
                if (dist[j] > next) { dist[j] = next; q.Enqueue(j); }
            }
            if (x + 1 < w)
            {
                int j = i + 1;
                if (dist[j] > next) { dist[j] = next; q.Enqueue(j); }
            }
            if (y > 0)
            {
                int j = i - w;
                if (dist[j] > next) { dist[j] = next; q.Enqueue(j); }
            }
            if (y + 1 < h)
            {
                int j = i + w;
                if (dist[j] > next) { dist[j] = next; q.Enqueue(j); }
            }
        }
        return dist;
    }

    /// <summary>Per cell: whether the arrival walks to it (8 neighbours, so a diagonal squeeze counts). Null when the
    /// arrival is not itself a walkable cell, where the walk map cannot say which way the player comes in.</summary>
    public static bool[]? Reachable(int w, int h, Func<int, int, bool> walkable, (int X, int Y) arrival)
    {
        if (w <= 0 || h <= 0) return null;
        var cells = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) cells[y * w + x] = walkable(x, y);
        return Reachable(w, h, cells, arrival, null);
    }

    static bool[]? Reachable(int w, int h, bool[] walkable, (int X, int Y) arrival,
        IReadOnlyList<((int X, int Y) From, (int X, int Y) To)>? localTransitions)
    {
        if (w <= 0 || h <= 0 || (uint)arrival.X >= (uint)w || (uint)arrival.Y >= (uint)h || !walkable[arrival.Y * w + arrival.X])
            return null;
        var seen = new bool[w * h];
        var q = new Queue<int>();
        bool Floor((int X, int Y) p) => (uint)p.X < (uint)w && (uint)p.Y < (uint)h && walkable[p.Y * w + p.X];
        var gates = (localTransitions ?? []).Where(t => Floor(t.From) && Floor(t.To))
            .GroupBy(t => t.From.Y * w + t.From.X)
            .ToDictionary(g => g.Key, g => g.Select(t => t.To.Y * w + t.To.X).Distinct().ToArray());
        seen[arrival.Y * w + arrival.X] = true;
        q.Enqueue(arrival.Y * w + arrival.X);
        while (q.Count > 0)
        {
            int i = q.Dequeue(), x = i % w, y = i / w;
            // A gate is an edge only after its entrance is reached; an isolated destination alone is not a new seed.
            if (gates.TryGetValue(i, out var destinations))
                foreach (int destination in destinations)
                    if (!seen[destination]) { seen[destination] = true; q.Enqueue(destination); }
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = y + dy;
                if ((uint)ny >= (uint)h) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx;
                    if ((uint)nx >= (uint)w) continue;
                    int j = ny * w + nx;
                    if (seen[j] || !walkable[j]) continue;
                    seen[j] = true;
                    q.Enqueue(j);
                }
            }
        }
        return seen;
    }

    /// <summary>
    /// The monster points of the rooms the client placed in this (area, seed): each room of data/room-points.json listed
    /// for the area is looked for among the doodads scripts/walkmaps captured (&lt;area&gt;_seed&lt;seed&gt;.doodads.json, grid
    /// cells). A placement is one of the 8 turns/flips of the room plus an offset; it is anchored on the room doodad whose
    /// model is the rarest in the capture, and kept when at least <see cref="FitShare"/> of the room's doodads land within
    /// <see cref="FitTolerance"/> cells of a captured doodad of the same model. The points go through the same placement.
    /// Without either file: none (notes say why).
    /// </summary>
    public static List<RoomPoint> LoadPoints(string areaId, uint seed, List<string> notes) => FitRooms(areaId, seed, notes, "data").Points;

    /// <summary>The server objects of the rooms placed in this (area, seed), found like <see cref="LoadPoints"/>.</summary>
    public static List<RoomObject> LoadObjects(string areaId, uint seed, List<string> notes) => FitRooms(areaId, seed, notes, "data").Objects;

    /// <summary>The fit of <see cref="LoadPoints"/> over a data folder's walkability/ captures (with client.db's room points), and the
    /// rooms that took their actual placement (captured or generated) instead: boss rooms (and
    /// <see cref="MercyMission.BoatRooms"/>) for their points, quest rooms for their objects. With
    /// <paramref name="generated"/>, the Gateway's own generation of the seed is used even where a capture exists (the
    /// capture tour's records compare the two fits).</summary>
    public static (List<RoomPoint> Points, List<RoomObject> Objects, List<string> Captured) FitRooms(string areaId, uint seed, List<string> notes,
        string folder, bool generated = false, List<PlacedRoom>? placedRooms = null)
    {
        string walkability = Path.Combine(folder, "walkability");
        string capture = Path.Combine(walkability, $"{areaId}_seed{seed}.doodads.json");
        if (RoomFits is not { } rooms) { notes.Add("no room_point_* tables in client.db (scripts/re/atlas room-points, then client-sql.ps1 diff)"); return ([], [], []); }
        if (!rooms.Areas.TryGetValue(areaId, out var candidates)) return ([], [], []);
        // Without a capture, an area the Gateway generates itself has the same doodads from the port (ServerLevels).
        var level = folder == "data" && (generated || !File.Exists(capture)) ? ServerLevels.For(areaId, seed) : null;
        if (generated && level is null) { notes.Add($"{areaId} seed {seed} is not generated by the Gateway"); return ([], [], []); }
        if (!File.Exists(capture) && level is null) { notes.Add($"no {capture} (live.ps1 -Walkmaps, then enter the area): no room points"); return ([], [], []); }
        // Which rooms the client placed, and where: the generated layout's, or the recorder's <area>_seed<seed>.rooms.json.
        bool placementsKnown = level is not null || File.Exists(Path.Combine(walkability, $"{areaId}_seed{seed}.rooms.json"));
        // Per lower-case model: the cells a doodad of it is placed on, and the cells within FitTolerance of one (a fit's
        // test is one lookup: a generated Voyage tests ~30 million doodad positions).
        var placed = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        var near = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        var byModel = new Dictionary<string, List<(int X, int Y)>>(StringComparer.OrdinalIgnoreCase);
        static long Cell(int x, int y) => ((long)x << 32) | (uint)y;
        void Add(string model, int x, int y)
        {
            string key = model.ToLowerInvariant();
            if (!placed.TryGetValue(key, out var cells)) { placed[key] = cells = []; near[key] = []; }
            if (!cells.Add(Cell(x, y))) return;
            var around = near[key];
            for (int dy = -FitTolerance; dy <= FitTolerance; dy++)
                for (int dx = -FitTolerance; dx <= FitTolerance; dx++)
                    around.Add(Cell(x + dx, y + dy));
            if (!byModel.TryGetValue(model, out var list)) byModel[model] = list = [];
            list.Add((x, y));
        }
        try
        {
            if (level is not null)
                foreach (var d in level.Doodads) Add(d.Model, d.X, d.Y);
            else
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(capture));
                var models = doc.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetString() ?? "").ToArray();
                foreach (var d in doc.RootElement.GetProperty("doodads").EnumerateArray())
                    Add(models[d[0].GetInt32()], d[1].GetInt32(), d[2].GetInt32());
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            notes.Add($"{capture}: {ex.Message}");
            return ([], [], []);
        }
        var points = new List<RoomPoint>();
        var objects = new List<RoomObject>();
        var capturedRooms = new List<string>();
        foreach (var path in candidates)
        {
            if (!rooms.Rooms.TryGetValue(path, out var room)) continue;
            // Named tutorial spawn points have no ordinary room doodads to fit. Use their actual captured .arm
            // placement. Existing monster-room fitting remains unchanged.
            if (room.Points.Any(p=>p.Name==TutorialObjects.Chest) && TutorialObjects.RoomPlacements(areaId,seed,path,walkability,level is not null) is { Count:>0 } captured)
            {
                foreach(var placement in captured)
                    foreach(var point in room.Points)
                        points.Add(placement.Place(point));
                continue;
            }
            int fitted = points.Count, fittedObjects = objects.Count;
            // Only the client's own doodads are in the capture: a room's server objects (an NPC, a quest blocker) are not.
            var plain = room.Doodads.Where(d => d.Type is null).ToList();
            var seen = plain.Where(d => byModel.ContainsKey(d.Model)).ToList();
            var origins = new List<(int X, int Y)>();
            var anchor = seen.Count == 0 ? default : seen.MinBy(d => byModel[d.Model].Count);
            // Each plain doodad's cells near a placed doodad of its model (null: none placed), and the hits a placement needs.
            var nearOf = plain.Select(d => near.GetValueOrDefault(d.Model.ToLowerInvariant())).ToArray();
            int need = Math.Max(4, (int)Math.Ceiling(FitShare * plain.Count));
            foreach (var c in seen.Count == 0 || nearOf.Count(n => n is not null) < need ? [] : byModel[anchor.Model])
                foreach (var (a, b, cc, dd) in Turns)
                {
                    // grid = origin + M · local, M = [[a, b], [cc, dd]]
                    int ox = c.X - (int)Math.Round(a * anchor.X + b * anchor.Y), oy = c.Y - (int)Math.Round(cc * anchor.X + dd * anchor.Y);
                    (int X, int Y) At(float lx, float ly) => (ox + (int)Math.Round(a * lx + b * ly), oy + (int)Math.Round(cc * lx + dd * ly));
                    // Given up once it can no longer reach the share; only a kept placement's count is printed.
                    int hits = 0, misses = 0;
                    for (int i = 0; i < plain.Count && misses <= plain.Count - need; i++)
                    {
                        var g = At(plain[i].X, plain[i].Y);
                        if (nearOf[i]?.Contains(Cell(g.X, g.Y)) == true) hits++; else misses++;
                    }
                    if (hits < need) continue;
                    if (origins.Any(o => Math.Abs(o.X - ox) <= 3 && Math.Abs(o.Y - oy) <= 3)) continue;
                    origins.Add((ox, oy));
                    foreach (var p in room.Points)
                    {
                        var g = At(p.X, p.Y);
                        points.Add(new RoomPoint(p.Name, g.X, g.Y, TurnAngle(a, b, cc, dd, p.Angle)) { Room = path });
                    }
                    foreach (var d in room.Doodads.Where(d => d.Type is not null))
                    {
                        var g = At(d.X, d.Y);
                        objects.Add(new RoomObject(d.Type!, d.Model, g.X, g.Y, TurnAngle(a, b, cc, dd, d.Angle), d.Scale, d.Height));
                    }
                    notes.Add($"room {path.Split('/')[^1]} at ({ox}, {oy}) turn [{a} {b}; {cc} {dd}]: {hits}/{plain.Count} doodads match; " +
                        string.Join(", ", room.Points.Select(p => $"{p.Name} at {At(p.X, p.Y)}")));
                }
            // A room with a unique boss point takes its actual placement, when the recorder captured it
            // (<area>_seed<seed>.rooms.json) or the Gateway generated the area, instead of the doodad fit: the fit misses
            // sparse rooms (The Tidal Island's medicine_boat rooms have only two snowfall emitters around Hailrake's
            // "mapboss"; The Upper Prison's boss_cells rooms with Brutus's "mapboss2") and matches repeated ones more than
            // once (four Necromancers in The Upper Prison), and each extra match would be one more unique boss. Other
            // rooms keep the fit: their points would add what the fit never placed (e.g. The Mud Flats' nest packs around
            // a1q4's Rhoa Nests) and change start points; that needs its own review.
            // When the placements are known (a generated area, or a capture with its rooms.json), a boss or quest room
            // the layout did not place has none, even if its doodads fit: sibling rooms share them (The Broken Bridge's
            // broken_bridge and broken_bridge_left, The Wetlands' four bandit_boss rooms), and the unplaced sibling's fit
            // on the placed one was a second Kraityn, a third Oak.
            bool bossRoom = MercyMission.BoatRooms.Contains(path) || room.Points.Any(p => BossPoint(p.Name) is not null);
            if (bossRoom && TutorialObjects.RoomPlacements(areaId, seed, path, walkability, level is not null) is var placements
                && (placements.Count > 0 || placementsKnown))
            {
                points.RemoveRange(fitted, points.Count - fitted);
                foreach (var placement in placements)
                {
                    points.AddRange(room.Points.Select(p => placement.Place(p) with { Room = path }));
                    capturedRooms.Add(path);
                    notes.Add($"room {path.Split('/')[^1]} at its captured placement ({placement.X}, {placement.Y}) code {placement.Code}: " +
                        string.Join(", ", room.Points.Select(p => $"{p.Name} at ({placement.Place(p).X}, {placement.Place(p).Y})")));
                }
            }
            // A room with a quest server object (Fairgraves on his ship, Piety, a quest chest, MerveilExitBlocker) takes its
            // actual placement for its objects (its points keep the fit), when the recorder captured it or the Gateway
            // generated the area: the fit misses sparse rooms and matches a repeated one more than once, which would put a
            // quest's NPC or blocker where the client has none, or twice.
            if (room.Doodads.Any(d => d.Type is { } t && RoomFile.IsQuestObject(t))
                && TutorialObjects.RoomPlacements(areaId, seed, path, walkability, level is not null) is var questPlacements
                && (questPlacements.Count > 0 || placementsKnown))
            {
                objects.RemoveRange(fittedObjects, objects.Count - fittedObjects);
                foreach (var placement in questPlacements)
                {
                    int first = objects.Count;
                    foreach (var d in room.Doodads.Where(d => d.Type is not null))
                    {
                        var g = placement.Place(new RoomPoint(d.Type!, (int)MathF.Round(d.X), (int)MathF.Round(d.Y), d.Angle));
                        objects.Add(new RoomObject(d.Type!, d.Model, g.X, g.Y, g.Angle, d.Scale, d.Height));
                    }
                    placedRooms?.Add(new PlacedRoom(path, [.. room.Points.Select(placement.Place)], objects.GetRange(first, objects.Count - first)));
                    if (!capturedRooms.Contains(path)) capturedRooms.Add(path);
                    notes.Add($"room {path.Split('/')[^1]} at its captured placement ({placement.X}, {placement.Y}) code {placement.Code}: quest objects " +
                        string.Join(", ", objects.Skip(fittedObjects).Where(o => RoomFile.IsQuestObject(o.Type)).Select(o => $"{o.Type} at ({o.X}, {o.Y})")));
                }
            }
        }
        return (points, objects, capturedRooms);
    }

    /// <summary>A room angle through a turn/flip [[a, b], [c, d]] (grid = M · room): a turn adds its own angle, a flip
    /// mirrors the angle and adds half a turn, the same angle as TutorialObjects.RoomPlacement.Place's codes 4..7 (#236's
    /// town rooms, checked against the client). Here only the identity is checked live (The Twilight Strand's
    /// town_entrance_exclude.arm, whose captured doodads keep the room's angles mod 2π).</summary>
    public static float TurnAngle(int a, int b, int c, int d, float angle)
    {
        float turn = MathF.Atan2(c, a);
        if (a * d - b * c < 0) { angle = -angle; turn += MathF.PI; }
        float r = (turn + angle) % (2 * MathF.PI);
        return r < 0 ? r + 2 * MathF.PI : r;
    }

    // Placement tolerance (cells) and the share of a room's doodads that must match.
    const int FitTolerance = 2;
    const double FitShare = 0.7;
    // The 8 turns and flips of a room: grid offset = [[a, b], [c, d]] · room offset.
    static readonly (int, int, int, int)[] Turns =
        [(1, 0, 0, 1), (0, -1, 1, 0), (-1, 0, 0, -1), (0, 1, -1, 0), (-1, 0, 0, 1), (0, 1, 1, 0), (1, 0, 0, -1), (0, -1, -1, 0)];

    /// <summary>A room-local cell (a .arm point or doodad) in world cells, through the room placement's orientation code
    /// (the same <see cref="Turns"/> matrix the doodad fit uses).</summary>
    public static (int X, int Y) LocalToWorld(int roomX, int roomY, int code, float localX, float localY)
    {
        var (a, b, c, d) = Turns[code & 7];
        return (roomX + (int)Math.Round(a * localX + b * localY), roomY + (int)Math.Round(c * localX + d * localY));
    }

    sealed record RoomData(IReadOnlyList<RoomPoint> Points, IReadOnlyList<(string Model, float X, float Y, string? Type, float Angle, float Scale, float? Height)> Doodads);
    sealed record RoomIndex(Dictionary<string, List<string>> Areas, Dictionary<string, RoomData> Rooms);

    // client.db's room_point_areas, room_point_rooms, room_points and room_doodads (data/room-points.json from
    // scripts/re/atlas room-points): the rooms with monster or start points per area.
    static RoomIndex? RoomFits = default!;

    /// <summary>Nothing left to read: LoadGameData reads the room index at startup. Kept for the game module's warm-up.</summary>
    public static void Warm() { }

    static RoomIndex? LoadRoomIndex()
    {
        if (ClientTables.TableOrNull("room_point_rooms") is not { } roomRows) return null;
        var areas = ClientTables.Table("room_point_areas").ToDictionary(a => a.Text("area")!, a => a.Texts("rooms").ToList());
        var pointsOf = ClientTables.TableOrEmpty("room_points").By("room");
        var doodadsOf = ClientTables.TableOrEmpty("room_doodads").By("room");
        var rooms = roomRows.ToDictionary(r => r.Text("room")!, r => new RoomData(
            [.. pointsOf[r.Index].Select(p => new RoomPoint(p.Text("name") ?? "",
                (int)Math.Round(p.Real("x")), (int)Math.Round(p.Real("y")), (float)(p.RealOrNull("angle") ?? 0))),
                .. LabyrinthTrials.RoomPacks.GetValueOrDefault(r.Text("room")!, [])],
            [.. doodadsOf[r.Index].Select(d => (d.Text("model") ?? "", (float)d.Real("x"), (float)d.Real("y"), d.TextOrNull("type"),
                (float)(d.RealOrNull("angle") ?? 0), (float)(d.RealOrNull("scale") ?? 1), (float?)d.RealOrNull("z")))]),
            StringComparer.OrdinalIgnoreCase);
        return new RoomIndex(areas, rooms);
    }
}
