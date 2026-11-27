using GameServer.Core;
using GameServer.Modding;

namespace GameServer.Modules.Bestiary;

public static partial class BestiaryTables
{
    static readonly (uint Stat, string Family)[] Lures =
    [
        (Stat.MapAdditionalRedBeastFamilyWilds, "Mammals"),
        (Stat.MapAdditionalRedBeastFamilySands, "BirdsAndReptiles"),
        (Stat.MapAdditionalRedBeastFamilyCaverns, "InsectsAndArachnids"),
        (Stat.MapAdditionalRedBeastFamilyDeep, "SeaCreatures"),
    ];

    public sealed record CaptureDefinition(int Row, ushort Variety, int Group, string Family);
    static IReadOnlyDictionary<ushort, CaptureDefinition> captures = new Dictionary<ushort, CaptureDefinition>();
    static IReadOnlyDictionary<string, RedKind[]> lurePools = new Dictionary<string, RedKind[]>();
    public static RedKind? BlackMorrigan { get; private set; }
    public static CaptureDefinition? CaptureOf(ushort variety) => captures.GetValueOrDefault(variety);

    static void LoadScarabData()
    {
        var families = ClientTables.TableOrEmpty("BestiaryFamilies");
        var groups = ClientTables.TableOrEmpty("BestiaryGroups");
        var found = new Dictionary<ushort, CaptureDefinition>();
        foreach (var row in ClientTables.TableOrEmpty("BestiaryCapturableMonsters"))
        {
            if (row.Bool("IsDisabled") || row.IntOrNull("MonsterVarietiesKey") is not int variety
                || variety < 0 || variety > ushort.MaxValue || row.IntOrNull("BestiaryGroupsKey") is not int group
                || group < 0 || group >= groups.Count) continue;
            int family = groups[group].IntOrNull("BestiaryFamiliesKey") ?? -1;
            if (family < 0 || family >= families.Count) continue;
            found.TryAdd((ushort)variety, new(row.Index, (ushort)variety, group, families[family].Text("Id") ?? ""));
        }
        captures = found;
        redPool = [.. redPool.Select(WithCapture)];
        lurePools = Lures.ToDictionary(l => l.Family, l => redPool.Where(k => k.Family == l.Family).ToArray());
        var special = ClientTables.TableOrEmpty("BestiaryEncounters").FirstOrDefault(r => r.TextOrNull("Id") == "AtlasMorriganBeast");
        BlackMorrigan = special.Table is not null && special.IntOrNull("MonsterVarietiesKey") is int key && key >= 0 && key < Monsters.All.Count
            && Monsters.All[key] is { Supported: true } varietyRow
            ? WithCapture(new(varietyRow.Id, (ushort)key, []) { Unique = true }) : null;

        RedKind WithCapture(RedKind kind) => found.TryGetValue(kind.Row, out var capture)
            ? kind with { Family = capture.Family, CapturableRow = capture.Row } : kind;
    }

    public static IReadOnlyList<RedKind> LurePool(string family) => lurePools.GetValueOrDefault(family) ?? [];

    public static bool ForcesEncounter(IReadOnlyDictionary<uint, int> stats) =>
        stats.GetValueOrDefault(Stat.MapBestiaryLeague) > 0;

    public static Plan? PlanFor(string areaId, int level, Random random, IReadOnlyDictionary<uint, int> stats)
    {
        if (PlanFor(areaId, level, random) is not { } ordinary) return null;
        var reds = ordinary.Reds.ToList();
        Add(Math.Max(0, stats.GetValueOrDefault(Stat.MapAdditionalRedBeasts)), redPool);
        foreach (var lure in Lures) Add(Math.Max(0, stats.GetValueOrDefault(lure.Stat)), LurePool(lure.Family));
        if (stats.GetValueOrDefault(Stat.MapBestiaryAreaContainsBlackMorrigan) > 0 && BlackMorrigan is { } morrigan)
            reds.Add(morrigan);
        return ordinary with { Reds = reds };

        void Add(int count, IReadOnlyList<RedKind> pool)
        {
            if (pool.Count == 0) return;
            // Bound invalid/custom area stats; all catalog scarab combinations are far below this placement budget.
            int bounded = Math.Min(count, Math.Max(0, 256 - reds.Count));
            if (bounded < count) Console.WriteLine($"[bestiary] additional red-beast placement budget exceeded: requested {count}, placed {bounded}");
            for (int i = 0; i < bounded; i++) reds.Add(pool[random.Next(pool.Count)]);
        }
    }

    public static void Opening(MapOpening request)
    {
        if (request.Campaign || request.Opening is not { } opening || LeagueRow < 0
            || LeagueChances.Current.ChanceOf(opening.Area.Id, LeagueRow, alternate: false).Chance <= 0
            || MissionFor(opening.Area.Level) is null
            || redPool.Count == 0) return;
        foreach (var lure in Lures)
            if (opening.Mods.Get(lure.Stat) > 0 && LurePool(lure.Family).Count == 0) return;
        if (opening.Mods.Get(Stat.MapBestiaryAreaContainsBlackMorrigan) > 0 && BlackMorrigan is null) return;
        request.HandledContent.Add("Bestiary");
    }
}
