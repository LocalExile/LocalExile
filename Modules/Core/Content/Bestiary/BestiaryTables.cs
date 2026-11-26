using GameServer.Core;

namespace GameServer.Modules.Bestiary;

/// <summary>
/// The client tables a Bestiary mission reads, loaded once with the module (<see cref="LoadGameData"/>) and looked up by Id,
/// never by a row number.
/// </summary>
public static partial class BestiaryTables
{
    /// <summary>One EinharMissions row. The table names none of its columns, so the meaning of each is read from its values
    /// over all 12 rows: <c>__col1</c>/<c>__col2</c> are the area levels the row holds (the rows tile 10 to 100 with no gap or
    /// overlap), <c>__col3</c>/<c>__col4</c> the yellow beast count's lowest and highest (3 to 4, 3 to 5, 3 to 6, never
    /// reversed), <c>__col7</c> the red beast count (0 for the rows below level 68, 1 for the three map rows). <c>__col5</c>
    /// (always 100) and <c>__col6</c> (0, then 4, 6, 8 over the map rows) are not read [ESTIMATE for the whole mapping: no
    /// official capture or schema names these fields].</summary>
    public sealed record Mission(string Id, int Lo, int Hi, int YellowMin, int YellowMax, int Red);

    /// <summary>A red beast kind: its variety and the minions of its pack (BestiaryEncounters.MonsterPacksKey →
    /// MonsterPackEntries rows whose Flag is 1; the Flag 0 row is the beast itself).</summary>
    public sealed record RedKind(string Id, ushort Row, IReadOnlyList<(ushort Row, int Weight)> Minions)
    {
        public string Family { get; init; } = "";
        public int CapturableRow { get; init; } = -1;
        public bool Unique { get; init; }
    }

    /// <summary>What an area's mission holds: the EinharMissions row, the red beast kinds drawn, and the yellow beasts' count
    /// with the varieties they are drawn from.</summary>
    public sealed record Plan(Mission Mission, IReadOnlyList<RedKind> Reds, int Yellow, IReadOnlyList<(ushort Row, int Weight)> YellowPool);

    static IReadOnlyList<Mission> missions = [];
    static int leagueRow = -1;
    static IReadOnlyList<RedKind> redPool = [];
    static HashSet<ushort> capturable = [];
    static IReadOnlyList<(ushort Row, int Weight)> fallback = [];

    /// <summary>The tag whose EinharPackFallback row is the maps' own (3.29.3c Tags: einharmaps; its packs are the
    /// chaos-goatmen ones).</summary>
    public const string FallbackTag = "einharmaps";

    /// <summary>The CoreLeagues row of the Bestiary, looked up by its Id (a bit of MapDisableLeagues).</summary>
    public static int LeagueRow => leagueRow;

    /// <summary>Reads the module's tables; called from the module's <c>Load</c>.</summary>
    public static void LoadGameData()
    {
        leagueRow = ClientTables.TableOrEmpty("CoreLeagues").FirstOrDefault(r => r.TextOrNull("Id") == "Bestiary").Index;
        missions = [.. ClientTables.TableOrEmpty("EinharMissions").Select(r => new Mission(
            r.TextOrNull("Id") ?? "", r.IntOrNull("__col1") ?? 0, r.IntOrNull("__col2") ?? 0,
            r.IntOrNull("__col3") ?? 0, r.IntOrNull("__col4") ?? 0, r.IntOrNull("__col7") ?? 0))];
        var all = Monsters.All;
        bool Writable(int? key) => key is >= 0 && key < all.Count && all[key.Value] is { Supported: true };
        var entries = new Dictionary<int, List<(int Flag, ushort Row, int Weight)>>();
        foreach (var e in ClientTables.TableOrEmpty("MonsterPackEntries"))
            if (e.IntOrNull("MonsterPacksKey") is int pack && e.IntOrNull("MonsterVarietiesKey") is int variety && Writable(variety))
            {
                if (!entries.TryGetValue(pack, out var list)) entries[pack] = list = [];
                list.Add((e.IntOrNull("Flag") ?? 0, (ushort)variety, e.IntOrNull("Weight") ?? 0));
            }
        // The red beast is one of the Menagerie's own beasts: BestiaryEncounters also lists the four spirit bosses, the
        // Harvest beasts and the Azmeri gull, none of which a mission spawns. The table has no weights, so each is as likely.
        redPool = [.. ClientTables.TableOrEmpty("BestiaryEncounters")
            .Where(r => (r.TextOrNull("MonsterSpawnerId") ?? "").StartsWith("Metadata/Monsters/LeagueBestiary/MenagerieSpawners/", StringComparison.Ordinal)
                && !(r.TextOrNull("Id") ?? "").EndsWith("SpiritBoss", StringComparison.Ordinal)
                && Writable(r.IntOrNull("MonsterVarietiesKey")))
            .Select(r => new RedKind(all[r.Int("MonsterVarietiesKey")].Id, (ushort)r.Int("MonsterVarietiesKey"),
                r.IntOrNull("MonsterPacksKey") is int pack && entries.TryGetValue(pack, out var members)
                    ? [.. members.Where(m => m.Flag == 1 && m.Weight > 0).Select(m => (m.Row, m.Weight))] : []))
            .DistinctBy(k => k.Row)];
        capturable = [.. ClientTables.TableOrEmpty("BestiaryCapturableMonsters")
            .Where(r => (r.IntOrNull("IsDisabled") ?? 0) == 0 && Writable(r.IntOrNull("MonsterVarietiesKey")))
            .Select(r => (ushort)r.Int("MonsterVarietiesKey"))];
        // EinharPackFallback: per Tags row the MonsterPacks Einhar uses when an area's own packs hold no beast. Which tag an
        // area takes is not in the tables; every map area takes the maps' own row [ESTIMATE].
        var tags = ClientTables.TableOrEmpty("Tags");
        var rows = ClientTables.TableOrEmpty("EinharPackFallback").Where(r => r.IntOrNull("Tags") is int tag && tag >= 0
            && tag < tags.Count && tags[tag].TextOrNull("Id") == FallbackTag);
        fallback = [.. rows.SelectMany(r => r.Ints("MonsterPack"))
            .SelectMany(pack => entries.GetValueOrDefault(pack) ?? [])
            .Where(m => m.Flag == 1 && m.Weight > 0 && capturable.Contains(m.Row))
            .GroupBy(m => m.Row).Select(g => (g.Key, g.Sum(m => m.Weight)))];
        LoadScarabData();
    }

    /// <summary>The EinharMissions row whose level range holds the area level, or null.</summary>
    public static Mission? MissionFor(int level) => missions.FirstOrDefault(m => m.Lo <= level && level <= m.Hi);

    /// <summary>The red beast kinds the server can write.</summary>
    public static IReadOnlyList<RedKind> RedKinds => redPool;

    /// <summary>Whether a variety row is in BestiaryCapturableMonsters (and not disabled).</summary>
    public static bool IsCapturable(ushort variety) => capturable.Contains(variety);

    /// <summary>The yellow beasts an area can hold: the capturable varieties of its own monster packs (each pack's area weight
    /// times its entry's weight), or, when it has none, the fallback packs' (<see cref="FallbackTag"/>), so every area can
    /// complete.</summary>
    public static IReadOnlyList<(ushort Row, int Weight)> YellowPool(string areaId)
    {
        var own = (Packs.For(areaId)?.Scattered ?? [])
            .SelectMany(s => s.Pack.Entries.Where(e => e.Weight > 0 && e.Variety is >= 0 and <= ushort.MaxValue && capturable.Contains((ushort)e.Variety))
                .Select(e => ((ushort)e.Variety, Weight: s.Weight * e.Weight)))
            .GroupBy(e => e.Item1).Select(g => (g.Key, g.Sum(e => e.Weight))).ToList();
        return own.Count > 0 ? own : fallback;
    }

    /// <summary>One entry of a weighted list, by the roll.</summary>
    public static ushort Draw(IReadOnlyList<(ushort Row, int Weight)> pool, Random rng)
    {
        int roll = rng.Next(Math.Max(1, pool.Sum(p => p.Weight)));
        foreach (var (row, weight) in pool) if ((roll -= weight) < 0) return row;
        return pool[^1].Row;
    }

    /// <summary>The mission of an area at a level: the row, the red beast kinds drawn (as many as the row's red count) and the
    /// yellow count with its pool. Null when the level has no row or nothing can be placed.</summary>
    public static Plan? PlanFor(string areaId, int level, Random rng)
    {
        if (MissionFor(level) is not { } mission) return null;
        var reds = new List<RedKind>();
        for (int i = 0; i < mission.Red && redPool.Count > 0; i++) reds.Add(redPool[rng.Next(redPool.Count)]);
        var pool = YellowPool(areaId);
        int yellow = pool.Count == 0 ? 0 : rng.Next(mission.YellowMin, Math.Max(mission.YellowMin, mission.YellowMax) + 1);
        return reds.Count + yellow == 0 ? null : new(mission, reds, yellow, pool);
    }
}
