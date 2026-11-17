using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Modules.Strongbox;

/// <summary>
/// How many strongboxes an area instance gets, which types, and where they stand. Strongboxes have no room in the level
/// generator (the CoreLeagues row "Ambush" has no ExtraTerrainFeatures row, so the generator never rolls it): the count
/// is the client's own league roll (<see cref="ExtraTerrainFeatures.LeagueCount"/> over the area's WorldAreaLeagueChances
/// row and the instance's area stats: <c>map_strongbox_chance_%</c>, <c>map_num_extra_strongboxes</c>,
/// <c>map_leaguestone_override_base_num_strongboxes</c>, and the exclusions <c>map_no_strongboxes</c>,
/// <c>map_disable_strongboxes_from_chance</c>, <c>map_disable_leagues</c>, <c>atlas_keystone_no_extra_content</c>, …) on
/// a stream of this module's own, derived from the instance seed: no generator draw moves.
/// </summary>
public static class StrongboxRules
{
    static uint StatKey(string name) => Stats.Key(name) ?? 0;

    /// <summary>A seed of this module's own for one purpose, from the instance seed.</summary>
    public static uint Seed(uint seed, string purpose) => unchecked(seed * 0x9e3779b1u ^ InstanceEntities.ObjectTypeHash("Strongbox/" + purpose));

    /// <summary>The CoreLeagues row of strongboxes ("Ambush"), or −1.</summary>
    static readonly Lazy<int> AmbushRow = new(() =>
    {
        foreach (var row in ClientTables.TableOrEmpty("CoreLeagues"))
            if (row.Text("Id") == "Ambush") return row.Index;
        return -1;
    });

    /// <summary>The count of the area's league roll, and the roll itself for the log; 0 for an area the roll cannot read.</summary>
    public static (int Count, string Roll) Count(string areaId, int level, uint seed, IReadOnlyDictionary<uint, int> stats)
    {
        if (AmbushRow.Value < 0) return (0, "no Ambush row in CoreLeagues");
        AreaInfo area;
        try
        {
            area = AreaInfo.Load(p => GamePack.Default.TryRead(p), areaId, level)
                with { Stats = stats.ToDictionary(s => (int)s.Key, s => s.Value) };
        }
        catch (Exception ex)
        {
            return (0, $"area row not read ({ex.GetType().Name}: {ex.Message})");
        }
        var s = ExtraTerrainFeatures.LeagueStruct(area, AmbushRow.Value);
        int count = ExtraTerrainFeatures.LeagueCount(area, new LevelRandom(Seed(seed, "count")), AmbushRow.Value);
        // Jumbled Abundance: each of its X additional contents is a strongbox, an imprisoned monster, a shrine or a rogue
        // exile; the strongboxes are this module's. INVENTED split: one in four (no source says how the X are divided).
        // Not when the area excludes strongboxes.
        int jumbled = 0;
        if (s.Max > 0 && area.Stat((int)StatKey("map_contains_X_additional_strongboxes_essences_shrines_or_rogue_exiles")) is int x and > 0)
        {
            var rng = new Random(unchecked((int)Seed(seed, "jumbled")));
            for (int i = 0; i < x; i++) if (rng.Next(4) == 0) jumbled++;
        }
        return (count + jumbled, $"chance {s.C0}%, follow-up {s.C1}%, bonus {s.C2}%, base {s.Base}, most {s.Max}" +
            (jumbled > 0 ? $", Jumbled Abundance +{jumbled}" : ""));
    }

    /// <summary>A box type by its weight (HardMode's weight in a HardMode league) × (100 + its weight stat) / 100, among
    /// the types whose level range holds the area level and that the server can create.</summary>
    public static StrongboxCatalog.Entry? Pick(IReadOnlyList<StrongboxCatalog.Entry> types, bool hardMode, int level,
        IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        // map_strongboxes_chance_to_be_operatives_%: an Operative's Strongbox (its weight is 0, so only this places one).
        if (stats.GetValueOrDefault(StatKey("map_strongboxes_chance_to_be_operatives_%")) is int operatives and > 0
            && rng.Next(100) < operatives
            && types.FirstOrDefault(t => t.Supported && t.Kind == StrongboxCatalog.Contents.Scarab
                && t.Type.EndsWith("HardMode", StringComparison.Ordinal) == hardMode && level >= t.MinLevel && level <= t.MaxLevel) is { } operative)
            return operative;
        // map_strongbox_rare_boxes_weighting_multiplier ("more likely to be rarer varieties"): × the rarer types' weight.
        int rarer = stats.GetValueOrDefault(StatKey("map_strongbox_rare_boxes_weighting_multiplier"));
        var pool = new List<(StrongboxCatalog.Entry Type, long Weight)>();
        foreach (var t in types)
        {
            if (!t.Supported || level < t.MinLevel || level > t.MaxLevel) continue;
            long weight = (long)(hardMode ? t.WeightHardMode : t.Weight)
                * Math.Max(0, 100 + (t.WeightStat != 0 ? stats.GetValueOrDefault(t.WeightStat) : 0)) / 100;
            if (t.Rarer && rarer > 1) weight *= rarer;
            if (weight > 0) pool.Add((t, weight));
        }
        long total = pool.Sum(p => p.Weight);
        if (total <= 0) return null;
        long roll = (long)(rng.NextDouble() * total);
        foreach (var (type, weight) in pool)
            if ((roll -= weight) < 0) return type;
        return pool[^1].Type;
    }

    /// <summary>How far (cells) a box stands from the arrival, and from an area transition, the waypoint and another box.</summary>
    public const int ArrivalClearance = 2 * AreaPopulation.TransitionClearance, Clearance = AreaPopulation.TransitionClearance,
        BoxSpacing = 2 * AreaPopulation.TransitionClearance;

    /// <summary>Room around a box's cell (<see cref="AreaLayout.WalkableFor"/>): the box and its ambush stand there.</summary>
    const int Room = 4;

    /// <summary>Up to <paramref name="count"/> cells for boxes: walkable with room around them, reachable on foot from the
    /// arrival, away from the arrival, the area transitions, the waypoint and each other, in a seeded order.</summary>
    public static List<(int X, int Y)> Spots(AreaLayout layout, int count, Random rng)
    {
        var spots = new List<(int X, int Y)>();
        if (count <= 0 || layout.Walkable is not { } walk || layout.GridW <= 0 || layout.GridH <= 0) return spots;
        int w = layout.GridW, h = layout.GridH;
        var arrival = layout.Near((layout.SpawnX, layout.SpawnY));
        var reached = Reached(walk, w, h, arrival);
        var avoid = layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY)).ToList();
        if (layout.Waypoint is { } waypoint) avoid.Add((waypoint.GridX, waypoint.GridY));
        const int stride = 4;
        var candidates = new List<(int X, int Y)>();
        for (int y = stride / 2; y < h; y += stride)
            for (int x = stride / 2; x < w; x += stride)
                if (reached[y * w + x] && Far((x, y), arrival, ArrivalClearance) && avoid.All(a => Far((x, y), a, Clearance)))
                    candidates.Add((x, y));
        for (int i = 0; i < candidates.Count && spots.Count < count; i++)
        {
            int j = i + rng.Next(candidates.Count - i);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            var c = candidates[i];
            if (layout.WalkableFor(c.X, c.Y, Room) && spots.All(s => Far(c, s, BoxSpacing))) spots.Add(c);
        }
        return spots;
    }

    static bool Far((int X, int Y) a, (int X, int Y) b, int distance) =>
        (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y) >= (long)distance * distance;

    /// <summary>The walkable cells reached from <paramref name="from"/> through the four neighbours.</summary>
    static bool[] Reached(Func<int, int, bool> walk, int w, int h, (int X, int Y) from)
    {
        var seen = new bool[w * h];
        if (from.X < 0 || from.Y < 0 || from.X >= w || from.Y >= h || !walk(from.X, from.Y)) return seen;
        var queue = new Queue<(int X, int Y)>();
        seen[from.Y * w + from.X] = true;
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (nx < 0 || ny < 0 || nx >= w || ny >= h || seen[ny * w + nx] || !walk(nx, ny)) continue;
                seen[ny * w + nx] = true;
                queue.Enqueue((nx, ny));
            }
        }
        return seen;
    }
}
