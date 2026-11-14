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
/// which no MonsterPacks row lists by area. Agents/docs/protocol/14-monster-packs.md.
/// </summary>
public sealed class PackCatalog(string folder = "data")
{
    public sealed record Pack(int Row, string Id, int MinCount, int MaxCount, int BossChance, int BossCount,
        IReadOnlyList<int> BossVarieties, IReadOnlyList<(int Variety, int Weight)> Entries, IReadOnlyDictionary<int, int> AreaWeights)
    {
        public int PickVariety(Random rng)
        {
            int roll = rng.Next(Math.Max(1, Entries.Sum(e => e.Weight)));
            foreach (var (variety, weight) in Entries) if ((roll -= weight) < 0) return variety;
            return Entries[^1].Variety;
        }
    }

    /// <summary>An area's packs: the scattered ones with their weights, the ones room points name (by the name after
    /// "&lt;area&gt;-"), and the unique bosses.</summary>
    public sealed record AreaPacks(string AreaId, int AreaLevel, IReadOnlyList<(Pack Pack, int Weight)> Scattered,
        IReadOnlyDictionary<string, Pack> Named, IReadOnlyList<int> Bosses)
    {
        /// <summary>Places in <see cref="Bosses"/> (0-based) this instance does not place with the area: the later forms a
        /// fight brings (<see cref="AreaPopulation.LaterForms"/>) and the bosses a map boss override leaves out
        /// (<see cref="MapBossOverride"/>).</summary>
        public IReadOnlySet<int> Absent { get; init; } = new HashSet<int>();

        /// <summary>How much denser than the ordinary its scattered packs stand (the Labyrinth's: LabyrinthTrials.PackDensity).</summary>
        public double Density { get; init; } = 1;
    }

    /// <summary>The weight of a map's own packs (Maps.dat MonsterPacksKeys), which have none for the area.</summary>
    public const int MapPackWeight = 100;

    private readonly Lazy<(List<Pack> Packs, List<(string Id, int Level, int[] Bosses)> Areas, Dictionary<int, List<int>> MapPacks)?> _data = new(() => Load(folder));

    public AreaPacks? For(string areaId)
    {
        if (_data.Value is not { } d) return null;
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
            int Row(string id) => VarietyRows.Value.GetValueOrDefault(id, -1);
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
        return new AreaPacks(areaId, d.Areas[row].Level, scattered, named, d.Areas[row].Bosses) { Density = density };
    }

    public Pack? Find(string id) => _data.Value?.Packs.FirstOrDefault(p => p.Id == id);

    // MonsterVarieties rows by Id, for the Labyrinth's packs.
    readonly Lazy<Dictionary<string, int>> VarietyRows = new(() =>
    {
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!ClientTables.Has("MonsterVarieties")) return rows;
        var doc = ClientTables.Table("MonsterVarieties");
        int row = 0;
        foreach (var v in doc) { if (v.Text("Id") is { } id) rows.TryAdd(id, row); row++; }
        return rows;
    });

    private static (List<Pack>, List<(string, int, int[])>, Dictionary<int, List<int>>)? Load(string folder)
    {
        if (!ClientTables.Has("MonsterPacks") || !ClientTables.Has("MonsterPackEntries") || !ClientTables.Has("WorldAreas")) return null;
        var byPack = new Dictionary<int, List<(int, int)>>();
        foreach (var e in ClientTables.Table("MonsterPackEntries"))
        {
            if (e.IsNull("MonsterPacksKey") || e.IsNull("MonsterVarietiesKey")) continue;
            int pack = e.Int("MonsterPacksKey");
            if (!byPack.TryGetValue(pack, out var list)) byPack[pack] = list = [];
            list.Add((e.Int("MonsterVarietiesKey"), e.Int("Weight")));
        }
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
                byPack.GetValueOrDefault(row) ?? [], areaWeights));
        }
        var areaRows = new List<(string, int, int[])>();
        foreach (var a in ClientTables.Table("WorldAreas"))
            areaRows.Add((a.Text("Id") ?? "", a.Int("AreaLevel"),
                [.. a.Ints("Bosses_MonsterVarietiesKeys")]));
        // Maps.dat: a map's area → its packs (optional table: without it maps have no packs of their own).
        var mapPacks = new Dictionary<int, List<int>>();
        if (ClientTables.Has("Maps"))
            foreach (var m in ClientTables.Table("Maps"))
                if (m.IntOrNull("Regular_WorldAreasKey") is int area)
                {
                    if (!mapPacks.TryGetValue(area, out var list)) mapPacks[area] = list = [];
                    list.AddRange([.. m.Ints("MonsterPacksKeys").Where(k => !list.Contains(k)).Distinct()]);
                }
        return (result, areaRows, mapPacks);
    }
}

/// <summary>
/// Where an area's monsters stand and what rarity they are. The tables give the packs and their weights; how many
/// packs an area has and how often a pack is magic or has a rare are decided by the official server and are not in the
/// client, so the numbers here are our own (<see cref="CellsPerPack"/>, <see cref="MagicChance"/>, <see cref="RareChance"/>).
/// </summary>
public static class AreaPopulation
{
    // Local population seed derivation. HashCode.Combine has a process-random salt and cannot reproduce instances.
    public static int PopulationSeed(ushort areaHash, uint areaSeed) => unchecked((int)InstanceEntities.ObjectTypeHash(
        FormattableString.Invariant($"population:{areaHash:x4}:{areaSeed:x8}")));

    public sealed record Spawn(int Variety, int X, int Y, MonsterRarity Rarity, string Pack);

    /// <summary>A named point of a room the client placed (found by <see cref="LoadPoints"/>).</summary>
    public sealed record RoomPoint(string Name, int X, int Y, float Angle = 0);

    /// <summary>A server object of a room the client placed (an NPC, a quest blocker): its type and model, grid cell,
    /// angle (radians) and scale, through the room's placement.</summary>
    /// <summary>A room's server object in grid cells; <paramref name="Height"/> is its line's fixed height (world z, down),
    /// null to stand on the terrain; <paramref name="Mirrored"/> whether its room is placed mirrored (orientation codes 4–7).</summary>
    public sealed record RoomObject(string Type, string Model, int X, int Y, float Angle, float Scale, float? Height = null, bool Mirrored = false);

    /// <summary>A quest room placed where the client placed it (<see cref="FitRooms"/>): its points and server objects
    /// in grid cells (a Trial of Ascendancy's room: its traps, its pack points and monsters).</summary>
    public sealed record PlacedRoom(string Path, IReadOnlyList<RoomPoint> Points, IReadOnlyList<RoomObject> Objects);

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
    // Cells kept free around the player's arrival point, between pack centres, and around area transitions.
    const int SpawnClearance = 60, PackSpacing = 28;
    /// <summary>Cells a shrine must stand clear of an entrance or waypoint to be guarded (also the clearance the scattered
    /// packs keep from an area transition).</summary>
    public const int TransitionClearance = 25;
    // A pack centre needs this many walkable cells to the nearest blocked cell; members stand within MemberRadius of it.
    const int CentreDepth = 5, MemberRadius = 9, MemberSpacing = 3;

    /// <summary>How far (cells) a shrine's guards may stand from it: outside the shrine's object size (the statue and its
    /// base) and a little wider than an ordinary pack, so a pack of four still fits around the shrine.</summary>
    public const int ShrineGuardRadius = 12;

    /// <summary>A map's pack size and extra packs (MapMods): Size multiplies both how many packs and how many members.</summary>
    public readonly record struct PackScale(double Size = 1, int ExtraNormal = 0, int ExtraMagic = 0, int ExtraRare = 0,
        int RareIncrease = 0, int MagicIncrease = 0);

    /// <param name="roomPacks">Points of rooms whose "normal", "magic" and "rare" points place one of the area's scattered
    /// packs of that rarity (a Trial of Ascendancy's rooms: the Lower Prison trial's plaque platform).</param>
    /// <param name="scatterable">Where a scattered pack may have its centre, besides the walk map (not among a trial's
    /// traps); null: anywhere.</param>
    public static List<Spawn> Plan(PackCatalog.AreaPacks area, int w, int h, Func<int, int, bool> walkable, (int X, int Y) arrival,
        IReadOnlyList<(int X, int Y)> transitions, IReadOnlyList<RoomPoint> points, int seed, Func<int, bool> spawnable, List<string> notes,
        PackScale scale = default, bool unassignedAtArrival = false, IReadOnlyList<RoomPoint>? roomPacks = null,
        Func<int, int, bool>? scatterable = null,
        IReadOnlyList<(string Shrine, ShrineGuards.Rule Rule, IReadOnlyList<PackCatalog.Pack> Packs, int X, int Y)>? shrineGuards = null)
    {
        var rng = new Random(seed);
        var spawns = new List<Spawn>();
        var taken = new HashSet<(int, int)>();
        var centres = new List<(int X, int Y)>();

        // The room points first. Each boss of WorldAreas Bosses stands once: at its own numbered point ("mapboss2"), in a
        // named pack whose boss it is (The Mines Level 1's "%area%-minerhammerbosspack": Pikerivet), or else, with every
        // other boss left, at one "mapboss" point (The Den: The Great White Bones with The Bone Sculptor; The Solaris
        // Temple Level 1: both seals). Bosses the instance leaves out (area.Absent: later forms, The Ashen Fields' map boss override) stand nowhere. The area's
        // boss pack stands once: at its own "%area%-bosspack" points when the layout has them, else at the first boss point
        // (before, every boss point added one: The Cavern of Anger, The Dried Lake and The Lunaris Temple Level 2 had it
        // up to three times).
        var placedRows = new HashSet<int>(area.Absent);
        bool guarded = points.Any(p => p.Name == "%area%-bosspack");
        void Bosses((int X, int Y) at, string point, IReadOnlyList<int> rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                int variety = area.Bosses[rows[i]];
                if (!spawnable(variety)) { notes.Add($"room point {point} at {at}: boss {variety} cannot be created"); continue; }
                if (AddMember(spawns, taken, walkable, at, variety, MonsterRarity.Unique, point, rng, i == 0 ? 0 : 3)) continue;
                // Some bosses' points are off the walk map (Kitava in his pit, Arakaali in her web, Doedre the Vile at her
                // cauldron): the nearest free walkable cell, so the server's walk and AI have a cell to start from.
                if (Nearest(at, walkable, taken, BossSearchRadius) is { } cell)
                {
                    taken.Add(cell);
                    spawns.Add(new Spawn(variety, cell.X, cell.Y, MonsterRarity.Unique, point));
                    notes.Add($"room point {point} at {at} is not walkable: boss {variety} at {cell}");
                }
                else notes.Add($"room point {point} at {at}: no free walkable cell within {BossSearchRadius} for boss {variety}");
            }
            if (!guarded && area.Named.TryGetValue("bosspack", out var guards))
            {
                AddPack(spawns, taken, walkable, at, guards, MonsterRarity.Normal, false, rng, spawnable);
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
            // A named pack's own boss (BossMonster_MonsterVarietiesKeys) comes with it; one of the area's unique bosses is
            // unique, and stands only once.
            int Row(int variety) => Enumerable.Range(0, area.Bosses.Count).FirstOrDefault(i => area.Bosses[i] == variety && !placedRows.Contains(i), -1);
            bool IsBoss(int variety) => area.Bosses.Contains(variety);
            foreach (int b in AddPack(spawns, taken, walkable, (p.X, p.Y), pack, MonsterRarity.Normal, true, rng, spawnable,
                b => !IsBoss(b) ? false : Row(b) >= 0 ? true : (bool?)null))
                placedRows.Add(Row(b));
            centres.Add((p.X, p.Y));
        }
        var rest = Enumerable.Range(0, area.Bosses.Count).Where(i => !placedRows.Contains(i)).ToList();
        var any = bossPoints.Where(b => b.Rows!.Count == 0).Select(b => b.Point).ToList();
        if (any.Count > 0 && rest.Count > 0)
        {
            var p = any[rng.Next(any.Count)];
            Bosses((p.X, p.Y), p.Name, rest);
        }
        else if (rest.Count > 0 && unassignedAtArrival)
        {
            notes.Add($"no room point for unique boss(es) {string.Join(", ", rest.Select(i => area.Bosses[i]))} of {area.AreaId}: standing at arrival");
            for (int i = 0; i < rest.Count; i++)
                Bosses((arrival.X + (i + 1) * 4, arrival.Y), "mapboss", [rest[i]]);
        }
        else if (rest.Count > 0 && bossPoints.Count > 0)
            notes.Add($"no room point for unique boss(es) {string.Join(", ", rest.Select(i => area.Bosses[i]))} of {area.AreaId}");

        // The scattered packs: random deep cells away from the arrival, the area transitions and other packs.
        var packs = area.Scattered.Where(s => s.Pack.Entries.Any(e => spawnable(e.Variety))).ToList();
        // Shrine guards (ShrineGuards): the pack rows the shrine's Shrines row names when it names any, else one of the
        // area's own packs at that pack's own count. One pack a shrine, normal rarity unless the rule says otherwise.
        foreach (var (shrine, rule, own, x, y) in shrineGuards ?? [])
        {
            if (!rule.Required) continue;
            var at = (X: x, Y: y);
            var guard = rule.AreaPool
                ? (packs.Count > 0 ? PickPack(packs, rng) : null)
                : own.Count > 0 ? own[rng.Next(own.Count)] : null;
            if (guard is null)
            {
                notes.Add($"shrine {shrine} at {at}: no pack of {area.AreaId} to guard it");
                continue;
            }
            // Around the shrine, never on it: the statue and its base stay clear by the shrine type's object size, and
            // the pack rings it a little further out than an ordinary pack's members stand.
            int first = spawns.Count;
            AddPack(spawns, taken, walkable, at, guard, rule.Rarity, true, rng, spawnable, memberLimit: rule.Fixed ?? int.MaxValue,
                keepClear: ShrinePlacement.ObjectSize, maxRadius: ShrineGuardRadius);
            centres.Add(at);
            var ring = spawns.Skip(first).Select(s => Math.Max(Math.Abs(s.X - at.X), Math.Abs(s.Y - at.Y))).ToList();
            notes.Add($"shrine {shrine} at {at}: guarded by {guard.Id} " +
                (rule.Fixed is int fixedCount ? $"({fixedCount})" : $"({guard.MinCount}\u2013{guard.MaxCount})") +
                (ring.Count == 0 ? ": no cell free around it" : $": {ring.Count} at {ring.Min()}\u2013{ring.Max()} cells"));
        }
        if (packs.Count == 0) return spawns;
        foreach (var p in roomPacks ?? [])
            if (p.Name switch { "normal" => MonsterRarity.Normal, "magic" => MonsterRarity.Magic, "rare" => MonsterRarity.Rare, _ => (MonsterRarity?)null }
                is { } rarity)
            {
                var pack = PickPack(packs, rng);
                AddPack(spawns, taken, walkable, (p.X, p.Y), pack, rarity, true, rng, spawnable);
                centres.Add((p.X, p.Y));
                notes.Add($"room point {p.Name} at ({p.X}, {p.Y}): {pack.Id}");
            }
        var depth = Depth(w, h, walkable);
        var candidates = new List<(int X, int Y)>();
        for (int y = 0; y < h; y += 3)
            for (int x = 0; x < w; x += 3)
                if (depth[y * w + x] >= CentreDepth) candidates.Add((x, y));
        int walkableCells = depth.Count(d => d > 0);
        double size = scale.Size > 0 ? scale.Size : 1;
        int wanted = (int)Math.Round(walkableCells / (double)CellsPerPack
            * SystemSettings.Default.MonsterPackDensityMultiplier * size * area.Density)
            + scale.ExtraNormal + scale.ExtraMagic + scale.ExtraRare;
        if (wanted <= 0) return spawns;
        Shuffle(candidates, rng);
        var plans = new List<((int X, int Y) At, PackCatalog.Pack Pack)>();
        foreach (var c in candidates)
        {
            if (plans.Count >= wanted) break;
            if (Near(c, arrival, SpawnClearance) || transitions.Any(t => Near(c, t, TransitionClearance)) || centres.Any(o => Near(c, o, PackSpacing))
                || scatterable is not null && !scatterable(c.X, c.Y)) continue;
            centres.Add(c);
            plans.Add((c, PickPack(packs, rng)));
        }
        // Rarity per pack: rare (one rare member), magic (all members) or normal; at least MinRares rares.
        var rarities = plans.Select(_ => RollRarity(rng.Next(100))).ToList();
        for (int i = 0; rarities.Count(r => r == MonsterRarity.Rare) < Math.Min(MinRares, plans.Count) && i < 100; i++)
            rarities[rng.Next(plans.Count)] = MonsterRarity.Rare;
        Promote(rarities, MonsterRarity.Rare, scale.ExtraRare + (int)Math.Round(plans.Count * scale.RareIncrease / 100.0));
        Promote(rarities, MonsterRarity.Magic, scale.ExtraMagic + (int)Math.Round(plans.Count * scale.MagicIncrease / 100.0));
        int scatteredStart = spawns.Count;
        int memberBudget = area.AreaId == TutorialObjects.Strand ? Math.Max(0, StrandMonsterTarget - 1 - scatteredStart) : int.MaxValue;
        int placedPacks = 0;
        for (int i = 0; i < plans.Count && spawns.Count - scatteredStart < memberBudget; i++)
        {
            AddPack(spawns, taken, walkable, plans[i].At, plans[i].Pack, rarities[i], true, rng, spawnable, packSize: size,
                memberLimit: memberBudget - (spawns.Count - scatteredStart));
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
        int memberLimit = int.MaxValue, int keepClear = 0, int maxRadius = MemberRadius)
    {
        int rolled = rng.Next(pack.MinCount, Math.Max(pack.MinCount, pack.MaxCount) + 1);
        int count = Math.Max(1, (int)Math.Round(rolled * SystemSettings.Default.MonsterPackSizeMultiplier * (packSize > 0 ? packSize : 1)));
        var members = new List<(int Variety, bool Unique)>();
        for (int i = 0; i < count; i++)
        {
            int v = pack.PickVariety(rng);
            if (spawnable(v)) members.Add((v, false));
        }
        // The pack's boss monster (Act1-CoastCrabs: 80% for one large Savage Crab; The Mines Level 1's
        // minerhammerbosspack: always Pikerivet).
        if (withBoss && pack.BossVarieties.Count > 0 && rng.Next(100) < pack.BossChance)
            for (int i = 0; i < pack.BossCount; i++)
                if (pack.BossVarieties[rng.Next(pack.BossVarieties.Count)] is int b && spawnable(b)
                    && (uniqueBoss is null ? false : uniqueBoss(b)) is bool unique && !members.Contains((b, true)))
                    members.Insert(0, (b, unique));
        var uniques = new List<int>();
        int before = spawns.Count;
        for (int i = 0; i < members.Count && spawns.Count - before < memberLimit; i++)
        {
            // Magic packs are magic throughout; a rare pack has one rare (the first member) among normal ones.
            var r = members[i].Unique ? MonsterRarity.Unique : rarity == MonsterRarity.Rare ? (i == 0 ? MonsterRarity.Rare : MonsterRarity.Normal) : rarity;
            if (AddMember(spawns, taken, walkable, at, members[i].Variety, r, pack.Id, rng,
                    keepClear > 0 ? keepClear : i == 0 ? 0 : 2, keepClear, maxRadius) && members[i].Unique) uniques.Add(members[i].Variety);
        }
        return uniques;
    }

    static bool AddMember(List<Spawn> spawns, HashSet<(int, int)> taken, Func<int, int, bool> walkable, (int X, int Y) at, int variety,
        MonsterRarity rarity, string pack, Random rng, int minRadius, int keepClear = 0, int maxRadius = MemberRadius)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            int radius = Math.Min(maxRadius, minRadius + attempt / 6);
            int x = at.X + rng.Next(-radius, radius + 1), y = at.Y + rng.Next(-radius, radius + 1);
            // A shrine's guards stand around it: no cell inside keepClear of the shrine's own cell.
            if (keepClear > 0 && Math.Max(Math.Abs(x - at.X), Math.Abs(y - at.Y)) < keepClear) continue;
            if (!walkable(x, y) || taken.Any(t => Math.Abs(t.Item1 - x) < MemberSpacing && Math.Abs(t.Item2 - y) < MemberSpacing)) continue;
            taken.Add((x, y));
            spawns.Add(new Spawn(variety, x, y, rarity, pack));
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
            if (Nearest((point.X, point.Y), walkable, [], BossSearchRadius) is { } cell) at[area.Bosses[i]] = cell;
        }
        return at;
    }

    /// <summary>The free walkable cell nearest to <paramref name="at"/> within <paramref name="radius"/> (square rings
    /// outwards, the closest of each ring by distance), or null.</summary>
    static (int X, int Y)? Nearest((int X, int Y) at, Func<int, int, bool> walkable, HashSet<(int, int)> taken, int radius)
    {
        bool Free(int x, int y) => walkable(x, y) && !taken.Any(t => Math.Abs(t.Item1 - x) < MemberSpacing && Math.Abs(t.Item2 - y) < MemberSpacing);
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

    static MonsterRarity RollRarity(int roll) =>
        roll < RareChance ? MonsterRarity.Rare : roll < RareChance + MagicChance ? MonsterRarity.Magic : MonsterRarity.Normal;

    static PackCatalog.Pack PickPack(List<(PackCatalog.Pack Pack, int Weight)> packs, Random rng)
    {
        int roll = rng.Next(packs.Sum(p => p.Weight));
        foreach (var (pack, weight) in packs) if ((roll -= weight) < 0) return pack;
        return packs[^1].Pack;
    }

    static bool Near((int X, int Y) a, (int X, int Y) b, int d) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y) < (long)d * d;

    static void Shuffle<T>(List<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
    }

    /// <summary>Per cell: 0 when blocked, else the 4-neighbour distance to the nearest blocked cell.</summary>
    static int[] Depth(int w, int h, Func<int, int, bool> walkable)
    {
        var dist = new int[w * h];
        var q = new Queue<int>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (walkable(x, y)) dist[y * w + x] = int.MaxValue; else q.Enqueue(y * w + x);
        while (q.Count > 0)
        {
            int i = q.Dequeue(), x = i % w, y = i / w;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int j = ny * w + nx;
                if (dist[j] > dist[i] + 1) { dist[j] = dist[i] + 1; q.Enqueue(j); }
            }
        }
        return dist;
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
        if (RoomFits.Value is not { } rooms) { notes.Add("no room_point_* tables in client.db (scripts/re/atlas room-points, then client-sql.ps1 diff)"); return ([], [], []); }
        if (!rooms.Areas.TryGetValue(areaId, out var candidates)) return ([], [], []);
        // Without a capture, an area the Gateway generates itself has the same doodads from the port (ServerLevels).
        var level = folder == "data" && (generated || !File.Exists(capture)) ? ServerLevels.For(areaId, seed) : null;
        if (generated && level is null) { notes.Add($"{areaId} seed {seed} is not generated by the Gateway"); return ([], [], []); }
        if (!File.Exists(capture) && level is null) { notes.Add($"no {capture} (live.ps1 -Walkmaps, then enter the area): no room points"); return ([], [], []); }
        // Which rooms the client placed, and where: the generated layout's, or the recorder's <area>_seed<seed>.rooms.json.
        bool placementsKnown = level is not null || File.Exists(Path.Combine(walkability, $"{areaId}_seed{seed}.rooms.json"));
        var placed = new HashSet<(string, int, int)>();
        var byModel = new Dictionary<string, List<(int X, int Y)>>(StringComparer.OrdinalIgnoreCase);
        void Add(string model, int x, int y)
        {
            if (!placed.Add((model.ToLowerInvariant(), x, y))) return;
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
        bool Near(string model, int x, int y)
        {
            for (int dy = -FitTolerance; dy <= FitTolerance; dy++)
                for (int dx = -FitTolerance; dx <= FitTolerance; dx++)
                    if (placed.Contains((model.ToLowerInvariant(), x + dx, y + dy))) return true;
            return false;
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
            foreach (var c in seen.Count == 0 ? [] : byModel[seen.MinBy(d => byModel[d.Model].Count).Model])
                foreach (var (a, b, cc, dd) in Turns)
                {
                    // grid = origin + M · local, M = [[a, b], [cc, dd]]
                    var anchor = seen.MinBy(d => byModel[d.Model].Count);
                    int ox = c.X - (int)Math.Round(a * anchor.X + b * anchor.Y), oy = c.Y - (int)Math.Round(cc * anchor.X + dd * anchor.Y);
                    (int X, int Y) At(float lx, float ly) => (ox + (int)Math.Round(a * lx + b * ly), oy + (int)Math.Round(cc * lx + dd * ly));
                    int hits = plain.Count(d => At(d.X, d.Y) is var g && Near(d.Model, g.X, g.Y));
                    if (hits < Math.Max(4, (int)Math.Ceiling(FitShare * plain.Count))) continue;
                    if (origins.Any(o => Math.Abs(o.X - ox) <= 3 && Math.Abs(o.Y - oy) <= 3)) continue;
                    origins.Add((ox, oy));
                    foreach (var p in room.Points)
                    {
                        var g = At(p.X, p.Y);
                        points.Add(new RoomPoint(p.Name, g.X, g.Y, TurnAngle(a, b, cc, dd, p.Angle)));
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
                    points.AddRange(room.Points.Select(placement.Place));
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
    static readonly Lazy<RoomIndex?> RoomFits = new(LoadRoomIndex);

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
