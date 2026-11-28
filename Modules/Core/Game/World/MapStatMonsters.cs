using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using static Game;

/// <summary>
/// Two client tables that tie a map stat to the area's monsters, for any map whose mods carry the stat:
/// <list type="bullet">
/// <item><c>MapInhabitants</c>: "Area is inhabited by …" (<c>map_packs_are_bone_husks</c>, <c>…_kitava_heralds</c>,
/// <c>…_porcupines</c>, <c>…_skeletons</c> …): every ordinary pack is replaced by one of the stat's packs, at its place
/// and rarity. With several such stats each pack draws from all their pools.</item>
/// <item><c>AdditionalMonstersFromMapStats</c>: "Area contains …" (<c>map_contains_the_twisted</c>, <c>…_the_feared</c>,
/// <c>…_evil_einhar</c>, <c>…_X_rogue_giants</c> …): unique monsters added where packs stand, away from the arrival.
/// <c>__col2</c> 0 adds every listed monster once; 1 adds the stat's value of them, distinct random picks when
/// <c>__col3</c> is 1, else in list order.</item>
/// </list>
/// </summary>
public static class MapStatMonsters
{
    const string InhabitantLabel = "Inhabitants/", ExtraLabel = "MapStatMonster/";
    sealed record Inhabitants(uint Stat, IReadOnlyList<PackCatalog.Pack> Packs);
    sealed record Additional(uint Stat, IReadOnlyList<int> Varieties, bool Counted, bool Random);

    static IReadOnlyList<Inhabitants> inhabitants = [];
    static IReadOnlyList<Additional> additional = [];
    static IReadOnlyList<Inhabitants> extraPacks = [];
    static IReadOnlyList<int> atlasBosses = [];

    /// <summary>"Area contains N additional packs of …" stats whose packs <c>AdditionalMonsterPacksFromStats</c> names
    /// and no other content adds (the Valdo modifiers'); each adds its value in packs.</summary>
    static readonly string[] PackStats =
    [
        "map_contains_additional_reviving_packs", "map_contains_frogs", "map_contains_x_additional_sulphite_golem_packs",
        "map_contains_X_additional_untainted_packs", "map_supporter_all_influence_packs",
    ];

    public static void LoadGameData()
    {
        var packs = ClientTables.TableOrEmpty("MonsterPacks").ToArray();
        inhabitants = [.. ClientTables.TableOrEmpty("MapInhabitants")
            .Where(r => r.IntOrNull("StatsKey") is not null)
            .Select(r => new Inhabitants((uint)r.Int("StatsKey") + 1,
                r.Ints("MonsterPacksKeys").Where(k => k >= 0 && k < packs.Length)
                    .Select(k => Game.Packs.Find(packs[k].Text("Id") ?? "")).OfType<PackCatalog.Pack>()
                    .Where(p => p.Entries.Count > 0).ToArray()))
            .Where(e => e.Packs.Count > 0)];
        var packStats = PackStats.Select(n => Program.Stats.Key(n)).OfType<uint>().ToHashSet();
        extraPacks = [.. ClientTables.TableOrEmpty("AdditionalMonsterPacksFromStats")
            .Where(r => r.IntOrNull("StatsKey") is int s && packStats.Contains((uint)s + 1) && r.IntOrNull("AdditionalMonsterPacksStatMode") == 0)
            .GroupBy(r => (uint)r.Int("StatsKey") + 1)
            .Select(g => new Inhabitants(g.Key, g.SelectMany(r => r.Ints("MonsterPacksKeys")).Distinct()
                .Where(k => k >= 0 && k < packs.Length).Select(k => Game.Packs.Find(packs[k].Text("Id") ?? ""))
                .OfType<PackCatalog.Pack>().Where(p => p.Entries.Count > 0).ToArray()))
            .Where(e => e.Packs.Count > 0)];
        // map_contains_additional_atlas_bosses: the bosses of the Atlas maps' own areas.
        atlasBosses = [.. ClientTables.TableOrEmpty("WorldAreas")
            .Where(r => (r.Text("Id") ?? "").StartsWith("MapWorlds", StringComparison.Ordinal))
            .SelectMany(r => r.Ints("Bosses_MonsterVarietiesKeys")).Distinct()];
        additional = [.. ClientTables.TableOrEmpty("AdditionalMonstersFromMapStats")
            .Where(r => r.IntOrNull("Stat") is not null)
            .Select(r => new Additional((uint)r.Int("Stat") + 1, r.Ints("Monsters").ToArray(),
                r.IntOrNull("__col2") == 1, r.IntOrNull("__col3") == 1))];
    }

    /// <summary>The extra packs of <see cref="PackStats"/> for the population's <c>additionalPacks</c>.</summary>
    public static IReadOnlyList<AreaPopulation.AdditionalPacks> Requests(MapMods? mods)
    {
        if (mods is null) return [];
        var result = new List<AreaPopulation.AdditionalPacks>();
        foreach (var entry in extraPacks)
            if (mods.Get(entry.Stat) is int count and > 0 && entry.Packs.Where(PackSupported).ToArray() is { Length: > 0 } pool)
                result.Add(new(ExtraLabel + Program.Stats.Name(entry.Stat), pool, count, mods.PackSizeFactor));
        return result;
    }

    static bool Supported(int row) => Game.Monsters.Find(row.ToString()) is { Supported: true };

    static bool PackSupported(PackCatalog.Pack pack) =>
        pack.Entries.Select(e => e.Variety).Concat(pack.BossVarieties).All(Supported);

    public static List<AreaPopulation.Spawn> Apply(IReadOnlyList<AreaPopulation.Spawn> source, MapMods? mods, uint seed,
        (int X, int Y) arrival, Func<int, int, bool> floor, List<string>? notes = null)
    {
        if (mods is null) return source.ToList();
        var result = ReplaceInhabitants(source, mods, seed, floor, notes);
        AddMonsters(result, mods, seed, arrival, notes);
        return result;
    }

    static List<AreaPopulation.Spawn> ReplaceInhabitants(IReadOnlyList<AreaPopulation.Spawn> source, MapMods mods, uint seed,
        Func<int, int, bool> floor, List<string>? notes)
    {
        var pool = inhabitants.Where(e => mods.Get(e.Stat) > 0).SelectMany(e => e.Packs).Where(PackSupported).Distinct().ToArray();
        if (pool.Length == 0)
        {
            if (inhabitants.Any(e => mods.Get(e.Stat) > 0)) notes?.Add("map inhabitants: no supported pack in the stat's pool");
            return source.ToList();
        }
        var rng = new Random(unchecked((int)seed ^ 0x494e4842));
        var replacements = new Dictionary<int, List<AreaPopulation.Spawn>>();
        foreach (var group in source.Where(s => s.PackId > 0).GroupBy(s => s.PackId).OrderBy(g => g.Key))
        {
            var members = group.ToArray();
            // Bosses and encounters' own packs stay; only the area's ordinary packs change.
            if (members.Any(s => s.Rarity == MonsterRarity.Unique || s.Pack.Contains('/'))) continue;
            var at = members[0];
            var made = AreaPopulation.AtPacks([(pool[rng.Next(pool.Length)], at.X, at.Y, members.Max(s => s.Rarity))], floor,
                Supported, rng.Next(), mods.PackSizeFactor);
            if (made.Count > 0) replacements[group.Key] = [.. made.Select(s => s with { PackId = group.Key, Pack = InhabitantLabel + s.Pack })];
        }
        notes?.Add($"map inhabitants: {replacements.Count} pack(s) replaced from {pool.Length} pack(s)");
        var result = new List<AreaPopulation.Spawn>();
        var emitted = new HashSet<int>();
        foreach (var spawn in source)
            if (!replacements.TryGetValue(spawn.PackId, out var replacement)) result.Add(spawn);
            else if (emitted.Add(spawn.PackId)) result.AddRange(replacement);
        return result;
    }

    static void AddMonsters(List<AreaPopulation.Spawn> spawns, MapMods mods, uint seed, (int X, int Y) arrival, List<string>? notes)
    {
        var rng = new Random(unchecked((int)seed ^ 0x41444d53));
        var wanted = new List<(uint Stat, int Variety)>();
        foreach (var entry in additional)
        {
            int value = mods.Get(entry.Stat);
            if (value <= 0 || entry.Varieties.Count == 0) continue;
            if (!entry.Counted) wanted.AddRange(entry.Varieties.Select(v => (entry.Stat, v)));
            else if (entry.Random)
                wanted.AddRange(entry.Varieties.OrderBy(_ => rng.Next()).Take(value).Select(v => (entry.Stat, v)));
            else
                for (int i = 0; i < value; i++) wanted.Add((entry.Stat, entry.Varieties[i % entry.Varieties.Count]));
        }
        if (mods.Get(Stat.MapContainsAdditionalAtlasBosses) is int bosses and > 0)
            wanted.AddRange(atlasBosses.Where(Supported).OrderBy(_ => rng.Next()).Take(bosses)
                .Select(v => (Stat.MapContainsAdditionalAtlasBosses, v)));
        if (wanted.Count == 0) return;
        // Where the area's packs stand, the farthest from the arrival first, one monster per pack.
        var places = spawns.Where(s => s.PackId > 0 && s.Rarity != MonsterRarity.Unique)
            .GroupBy(s => s.PackId).Select(g => g.First())
            .OrderByDescending(s => (long)(s.X - arrival.X) * (s.X - arrival.X) + (long)(s.Y - arrival.Y) * (s.Y - arrival.Y))
            .ToList();
        int half = Math.Max(1, places.Count / 2);
        var far = places.Take(half).OrderBy(_ => rng.Next()).ToList();
        int nextPack = spawns.Count == 0 ? 1 : spawns.Max(s => s.PackId) + 1, placed = 0, skipped = 0;
        foreach (var (stat, variety) in wanted)
        {
            if (!Supported(variety) || far.Count == 0) { skipped++; continue; }
            var at = far[placed % far.Count];
            spawns.Add(new AreaPopulation.Spawn(variety, at.X, at.Y, MonsterRarity.Unique, ExtraLabel + Program.Stats.Name(stat))
            {
                PackId = nextPack++,
            });
            placed++;
        }
        notes?.Add($"map stat monsters: {placed} added, {skipped} not supported or without a place");
    }
}
