using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The monster packs of an area, from the extracted tables: MonsterPacks (which areas and their weights, how many
/// monsters, the pack's boss), MonsterPackEntries (the varieties and their weights) and WorldAreas (the area level and
/// its unique boss, Bosses_MonsterVarietiesKeys). Packs with a weight for the area are scattered over the walk map;
/// packs named "&lt;area&gt;-…" (weight 0) stand at the room points of the layout that name them ("%area%-cannibalfire"
/// → "1_1_2-cannibalfire"); the room point "mapboss" takes the area's boss with the pack "&lt;area&gt;-bosspack". A map's
/// area (Maps.dat Regular_WorldAreasKey) also gets the map's own packs (Maps.dat MonsterPacksKeys: Beach → atlas-crab1),
/// which no MonsterPacks row lists by area. Agents/docs/protocol/14-monster-packs.md.
/// </summary>
sealed class PackCatalog(string folder = "data")
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
        IReadOnlyDictionary<string, Pack> Named, IReadOnlyList<int> Bosses);

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
        return new AreaPacks(areaId, d.Areas[row].Level, scattered, named, d.Areas[row].Bosses);
    }

    private static (List<Pack>, List<(string, int, int[])>, Dictionary<int, List<int>>)? Load(string folder)
    {
        string packs = Path.Combine(folder, "tables", "MonsterPacks.json"), entries = Path.Combine(folder, "tables", "MonsterPackEntries.json"),
            areas = Path.Combine(folder, "tables", "WorldAreas.json");
        if (!File.Exists(packs) || !File.Exists(entries) || !File.Exists(areas)) return null;
        var byPack = new Dictionary<int, List<(int, int)>>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(entries)))
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.GetProperty("MonsterPacksKey").ValueKind != JsonValueKind.Number || e.GetProperty("MonsterVarietiesKey").ValueKind != JsonValueKind.Number) continue;
                int pack = e.GetProperty("MonsterPacksKey").GetInt32();
                if (!byPack.TryGetValue(pack, out var list)) byPack[pack] = list = [];
                list.Add((e.GetProperty("MonsterVarietiesKey").GetInt32(), e.GetProperty("Weight").GetInt32()));
            }
        var result = new List<Pack>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(packs)))
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                int row = result.Count;
                // WorldAreasKeys with one weight each in __col9, or one weight for all of them (Act1-CoastCrabs: [6, 7], [100]).
                int[] areaKeys = [.. p.GetProperty("WorldAreasKeys").EnumerateArray().Select(a => a.GetInt32())];
                int[] weights = [.. p.GetProperty("__col9").EnumerateArray().Select(a => a.GetInt32())];
                var areaWeights = new Dictionary<int, int>();
                for (int i = 0; i < areaKeys.Length && weights.Length > 0; i++) areaWeights[areaKeys[i]] = weights[Math.Min(i, weights.Length - 1)];
                result.Add(new Pack(row, p.GetProperty("Id").GetString() ?? "", p.GetProperty("MinCount").GetInt32(), p.GetProperty("MaxCount").GetInt32(),
                    p.GetProperty("BossMonsterSpawnChance").GetInt32(), p.GetProperty("BossMonsterCount").GetInt32(),
                    [.. p.GetProperty("BossMonster_MonsterVarietiesKeys").EnumerateArray().Select(b => b.GetInt32())],
                    byPack.GetValueOrDefault(row) ?? [], areaWeights));
            }
        var areaRows = new List<(string, int, int[])>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(areas)))
            foreach (var a in doc.RootElement.EnumerateArray())
                areaRows.Add((a.GetProperty("Id").GetString() ?? "", a.GetProperty("AreaLevel").GetInt32(),
                    [.. a.GetProperty("Bosses_MonsterVarietiesKeys").EnumerateArray().Select(b => b.GetInt32())]));
        // Maps.dat: a map's area → its packs (optional table: without it maps have no packs of their own).
        var mapPacks = new Dictionary<int, List<int>>();
        string maps = Path.Combine(folder, "tables", "Maps.json");
        if (File.Exists(maps))
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(maps)))
                foreach (var m in doc.RootElement.EnumerateArray())
                    if (m.GetProperty("Regular_WorldAreasKey") is { ValueKind: JsonValueKind.Number } area)
                    {
                        if (!mapPacks.TryGetValue(area.GetInt32(), out var list)) mapPacks[area.GetInt32()] = list = [];
                        list.AddRange([.. m.GetProperty("MonsterPacksKeys").EnumerateArray().Select(k => k.GetInt32()).Where(k => !list.Contains(k)).Distinct()]);
                    }
        return (result, areaRows, mapPacks);
    }
}

/// <summary>
/// Where an area's monsters stand and what rarity they are. The tables give the packs and their weights; how many
/// packs an area has and how often a pack is magic or has a rare are decided by the official server and are not in the
/// client, so the numbers here are our own (<see cref="CellsPerPack"/>, <see cref="MagicChance"/>, <see cref="RareChance"/>).
/// </summary>
static class AreaPopulation
{
    public sealed record Spawn(int Variety, int X, int Y, MonsterRarity Rarity, string Pack);

    /// <summary>A named point of a room the client placed (found by <see cref="LoadPoints"/>).</summary>
    public sealed record RoomPoint(string Name, int X, int Y, float Angle = 0);

    /// <summary>A server object of a room the client placed (an NPC, a quest blocker): its type and model, grid cell,
    /// angle (radians) and scale, through the room's placement.</summary>
    public sealed record RoomObject(string Type, string Model, int X, int Y, float Angle, float Scale);

    static bool IsMonsterPoint(string name) => name == "mapboss" || name.StartsWith("%area%-", StringComparison.Ordinal);

    // One scattered pack per this many walkable cells (The Coast seed 1: 404 620 cells → 45 packs).
    public const int CellsPerPack = 9000;
    // Percent of scattered packs that are magic (every member), and that have one rare member (the rest stay normal).
    public const int MagicChance = 12, RareChance = 6;
    // At least this many packs with a rare in an area that has scattered packs.
    public const int MinRares = 2;
    // Cells kept free around the player's arrival point, between pack centres, and around area transitions.
    const int SpawnClearance = 60, PackSpacing = 28, TransitionClearance = 25;
    // A pack centre needs this many walkable cells to the nearest blocked cell; members stand within MemberRadius of it.
    const int CentreDepth = 5, MemberRadius = 9, MemberSpacing = 3;

    public static List<Spawn> Plan(PackCatalog.AreaPacks area, int w, int h, Func<int, int, bool> walkable, (int X, int Y) arrival,
        IReadOnlyList<(int X, int Y)> transitions, IReadOnlyList<RoomPoint> points, int seed, Func<int, bool> spawnable, List<string> notes)
    {
        var rng = new Random(seed);
        var spawns = new List<Spawn>();
        var taken = new HashSet<(int, int)>();
        var centres = new List<(int X, int Y)>();

        // The room points first: the unique boss with its pack at "mapboss", the named packs at theirs.
        foreach (var p in points.Where(p => IsMonsterPoint(p.Name)))
        {
            string name = p.Name.Replace("%area%-", "", StringComparison.Ordinal);
            if (name == "mapboss")
            {
                var bosses = area.Bosses.Where(spawnable).ToList();
                if (bosses.Count == 0) { notes.Add($"room point mapboss at ({p.X}, {p.Y}): no unique boss for {area.AreaId}"); continue; }
                AddMember(spawns, taken, walkable, (p.X, p.Y), bosses[rng.Next(bosses.Count)], MonsterRarity.Unique, "mapboss", rng, 0);
                if (area.Named.TryGetValue("bosspack", out var guards)) AddPack(spawns, taken, walkable, (p.X, p.Y), guards, MonsterRarity.Normal, false, rng, spawnable);
            }
            else if (area.Named.TryGetValue(name, out var pack))
                AddPack(spawns, taken, walkable, (p.X, p.Y), pack, MonsterRarity.Normal, false, rng, spawnable);
            else { notes.Add($"room point {p.Name} at ({p.X}, {p.Y}): no pack {area.AreaId}-{name}"); continue; }
            centres.Add((p.X, p.Y));
        }

        // The scattered packs: random deep cells away from the arrival, the area transitions and other packs.
        var packs = area.Scattered.Where(s => s.Pack.Entries.Any(e => spawnable(e.Variety))).ToList();
        if (packs.Count == 0) return spawns;
        var depth = Depth(w, h, walkable);
        var candidates = new List<(int X, int Y)>();
        for (int y = 0; y < h; y += 3)
            for (int x = 0; x < w; x += 3)
                if (depth[y * w + x] >= CentreDepth) candidates.Add((x, y));
        int walkableCells = depth.Count(d => d > 0);
        int wanted = Math.Max(1, walkableCells / CellsPerPack);
        Shuffle(candidates, rng);
        var plans = new List<((int X, int Y) At, PackCatalog.Pack Pack)>();
        foreach (var c in candidates)
        {
            if (plans.Count >= wanted) break;
            if (Near(c, arrival, SpawnClearance) || transitions.Any(t => Near(c, t, TransitionClearance)) || centres.Any(o => Near(c, o, PackSpacing))) continue;
            centres.Add(c);
            plans.Add((c, PickPack(packs, rng)));
        }
        // Rarity per pack: rare (one rare member), magic (all members) or normal; at least MinRares rares.
        var rarities = plans.Select(_ => RollRarity(rng.Next(100))).ToList();
        for (int i = 0; rarities.Count(r => r == MonsterRarity.Rare) < Math.Min(MinRares, plans.Count) && i < 100; i++)
            rarities[rng.Next(plans.Count)] = MonsterRarity.Rare;
        for (int i = 0; i < plans.Count; i++)
            AddPack(spawns, taken, walkable, plans[i].At, plans[i].Pack, rarities[i], true, rng, spawnable);
        notes.Add($"{plans.Count} scattered packs ({rarities.Count(r => r == MonsterRarity.Magic)} magic, {rarities.Count(r => r == MonsterRarity.Rare)} with a rare) " +
            $"for {walkableCells} walkable cells, {points.Count} room point(s), {spawns.Count} monsters");
        return spawns;
    }

    static void AddPack(List<Spawn> spawns, HashSet<(int, int)> taken, Func<int, int, bool> walkable, (int X, int Y) at, PackCatalog.Pack pack,
        MonsterRarity rarity, bool withBoss, Random rng, Func<int, bool> spawnable)
    {
        int count = rng.Next(pack.MinCount, Math.Max(pack.MinCount, pack.MaxCount) + 1);
        var members = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int v = pack.PickVariety(rng);
            if (spawnable(v)) members.Add(v);
        }
        // The pack's boss monster (Act1-CoastCrabs: 80% for one large Savage Crab).
        if (withBoss && pack.BossVarieties.Count > 0 && rng.Next(100) < pack.BossChance)
            for (int i = 0; i < pack.BossCount; i++)
                if (pack.BossVarieties[rng.Next(pack.BossVarieties.Count)] is int b && spawnable(b)) members.Insert(0, b);
        for (int i = 0; i < members.Count; i++)
        {
            // Magic packs are magic throughout; a rare pack has one rare (the first member) among normal ones.
            var r = rarity == MonsterRarity.Rare ? (i == 0 ? MonsterRarity.Rare : MonsterRarity.Normal) : rarity;
            AddMember(spawns, taken, walkable, at, members[i], r, pack.Id, rng, i == 0 ? 0 : 2);
        }
    }

    static void AddMember(List<Spawn> spawns, HashSet<(int, int)> taken, Func<int, int, bool> walkable, (int X, int Y) at, int variety,
        MonsterRarity rarity, string pack, Random rng, int minRadius)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            int radius = Math.Min(MemberRadius, minRadius + attempt / 6);
            int x = at.X + rng.Next(-radius, radius + 1), y = at.Y + rng.Next(-radius, radius + 1);
            if (!walkable(x, y) || taken.Any(t => Math.Abs(t.Item1 - x) < MemberSpacing && Math.Abs(t.Item2 - y) < MemberSpacing)) continue;
            taken.Add((x, y));
            spawns.Add(new Spawn(variety, x, y, rarity, pack));
            return;
        }
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

    /// <summary>The fit of <see cref="LoadPoints"/> over a data folder (its room-points.json and walkability/), and the
    /// rooms that took their captured placement instead (only <see cref="MercyMission.BoatRooms"/>).</summary>
    internal static (List<RoomPoint> Points, List<RoomObject> Objects, List<string> Captured) FitRooms(string areaId, uint seed, List<string> notes,
        string folder)
    {
        string walkability = Path.Combine(folder, "walkability");
        string capture = Path.Combine(walkability, $"{areaId}_seed{seed}.doodads.json");
        if ((folder == "data" ? RoomFits.Value : LoadRoomIndex(Path.Combine(folder, "room-points.json"))) is not { } rooms) { notes.Add("no data/room-points.json (dotnet run --project scripts/re/atlas -- Client/Content.ggpk room-points)"); return ([], [], []); }
        if (!rooms.Areas.TryGetValue(areaId, out var candidates)) return ([], [], []);
        if (!File.Exists(capture)) { notes.Add($"no {capture} (live.ps1 -Walkmaps, then enter the area): no room points"); return ([], [], []); }
        var placed = new HashSet<(string, int, int)>();
        var byModel = new Dictionary<string, List<(int X, int Y)>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(capture));
            var models = doc.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetString() ?? "").ToArray();
            foreach (var d in doc.RootElement.GetProperty("doodads").EnumerateArray())
            {
                string model = models[d[0].GetInt32()];
                int x = d[1].GetInt32(), y = d[2].GetInt32();
                if (!placed.Add((model.ToLowerInvariant(), x, y))) continue;
                if (!byModel.TryGetValue(model, out var list)) byModel[model] = list = [];
                list.Add((x, y));
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
            if (room.Points.Any(p=>p.Name==TutorialObjects.Chest) && TutorialObjects.RoomPlacements(areaId,seed,path,walkability) is { Count:>0 } captured)
            {
                foreach(var placement in captured)
                    foreach(var point in room.Points)
                        points.Add(placement.Place(point));
                continue;
            }
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
                    foreach (var p in room.Points) { var g = At(p.X, p.Y); points.Add(new RoomPoint(p.Name, g.X, g.Y)); }
                    foreach (var d in room.Doodads.Where(d => d.Type is not null))
                    {
                        var g = At(d.X, d.Y);
                        objects.Add(new RoomObject(d.Type!, d.Model, g.X, g.Y, TurnAngle(a, b, cc, dd, d.Angle), d.Scale));
                    }
                    notes.Add($"room {path.Split('/')[^1]} at ({ox}, {oy}) turn [{a} {b}; {cc} {dd}]: {hits}/{plain.Count} doodads match; " +
                        string.Join(", ", room.Points.Select(p => $"{p.Name} at {At(p.X, p.Y)}")));
                }
            // The Tidal Island's medicine_boat rooms have only two snowfall emitters around Hailrake's "mapboss", too few for
            // the fit: they take their actual placement, when the recorder captured it (<area>_seed<seed>.rooms.json). Only
            // these rooms: in other areas this would add points the fit never placed (e.g. The Mud Flats' miniboss and
            // nest packs around a1q4's Rhoa Nests) and change start points; that needs its own review.
            if (origins.Count == 0 && MercyMission.BoatRooms.Contains(path)
                && TutorialObjects.RoomPlacements(areaId, seed, path, walkability) is { Count: > 0 } placements)
                foreach (var placement in placements)
                {
                    points.AddRange(room.Points.Select(placement.Place));
                    capturedRooms.Add(path);
                    notes.Add($"room {path.Split('/')[^1]} at its captured placement ({placement.X}, {placement.Y}) code {placement.Code}: " +
                        string.Join(", ", room.Points.Select(p => $"{p.Name} at ({placement.Place(p).X}, {placement.Place(p).Y})")));
                }
        }
        return (points, objects, capturedRooms);
    }

    /// <summary>A room angle through a turn/flip [[a, b], [c, d]] (grid = M · room): a turn adds its own angle, a flip
    /// mirrors the angle first. Only the identity is checked live (The Twilight Strand's town_entrance_exclude.arm,
    /// whose captured doodads keep the room's angles mod 2π). For a flip this gives π − θ where
    /// TutorialObjects.RoomPlacement.Place (and #236's town rooms, checked against the client) give 2π − θ; the flips'
    /// matrices and codes are not matched here yet, so no mirrored room is relied on.</summary>
    public static float TurnAngle(int a, int b, int c, int d, float angle)
    {
        float turn = MathF.Atan2(c, a);
        if (a * d - b * c < 0) angle = -angle;
        float r = (turn + angle) % (2 * MathF.PI);
        return r < 0 ? r + 2 * MathF.PI : r;
    }

    // Placement tolerance (cells) and the share of a room's doodads that must match.
    const int FitTolerance = 2;
    const double FitShare = 0.7;
    // The 8 turns and flips of a room: grid offset = [[a, b], [c, d]] · room offset.
    static readonly (int, int, int, int)[] Turns =
        [(1, 0, 0, 1), (0, -1, 1, 0), (-1, 0, 0, -1), (0, 1, -1, 0), (-1, 0, 0, 1), (0, 1, 1, 0), (1, 0, 0, -1), (0, -1, -1, 0)];

    sealed record RoomData(IReadOnlyList<RoomPoint> Points, IReadOnlyList<(string Model, float X, float Y, string? Type, float Angle, float Scale)> Doodads);
    sealed record RoomIndex(Dictionary<string, List<string>> Areas, Dictionary<string, RoomData> Rooms);

    // data/room-points.json (scripts/re/atlas room-points): the rooms with monster or start points per area. Game data: stays in data/.
    static readonly Lazy<RoomIndex?> RoomFits = new(() => LoadRoomIndex("data/room-points.json"));

    static RoomIndex? LoadRoomIndex(string file)
    {
        if (!File.Exists(file)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        var areas = doc.RootElement.GetProperty("areas").EnumerateObject()
            .ToDictionary(a => a.Name, a => a.Value.EnumerateArray().Select(r => r.GetString()!).ToList());
        var rooms = doc.RootElement.GetProperty("rooms").EnumerateObject().ToDictionary(r => r.Name, r => new RoomData(
            [.. r.Value.GetProperty("points").EnumerateArray().Select(p => new RoomPoint(p.GetProperty("name").GetString() ?? "",
                (int)Math.Round(p.GetProperty("x").GetDouble()), (int)Math.Round(p.GetProperty("y").GetDouble()),
                p.TryGetProperty("angle",out var angle)?angle.GetSingle():0))],
            [.. r.Value.GetProperty("doodads").EnumerateArray().Select(d => (d.GetProperty("model").GetString() ?? "",
                (float)d.GetProperty("x").GetDouble(), (float)d.GetProperty("y").GetDouble(),
                d.TryGetProperty("type", out var t) ? t.GetString() : null,
                d.TryGetProperty("angle", out var an) ? (float)an.GetDouble() : 0f,
                d.TryGetProperty("scale", out var sc) ? (float)sc.GetDouble() : 1f))]), StringComparer.OrdinalIgnoreCase);
        return new RoomIndex(areas, rooms);
    }
}
