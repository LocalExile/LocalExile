using GameServer.Core;

public static partial class Game
{
    static IReadOnlyDictionary<int, ushort> DarkMonarchMinionTypes = new Dictionary<int, ushort>();

    static void LoadDarkMonarchData() =>
        DarkMonarchMinionTypes = ClientTables.TableOrEmpty("MinionDoublingStatTypes")
            .ToDictionary(row => row.Int("Id") + 1, row => checked((ushort)row.Int("MinionType")));

    static ushort? DarkMonarchSelection(IReadOnlyDictionary<uint, int> stats) =>
        DarkMonarchMinionTypes.TryGetValue(GolemStat(stats, Stat.MaximumNumberOfXMinionTypeDoubled), out ushort type)
            ? type : null;

    // Several minion types have separate client counters but belong to one selectable family.
    static ushort? DarkMonarchMinionFamily(string family) => family switch
    {
        "Spectre" => 0,
        "DominatingNormal" or "DominatingMagic" or "DominatingRare" => 1,
        "Zombie" => 7,
        "Skeleton" or "VaalSkeleton" => 9,
        "Phantasm" => 11,
        "AnimatedWeapon" or "VaalAnimatedWeapon" => 19,
        "RagingSpirit" => 20,
        "Golem" => 24,
        "Wolf" => 28,
        "Spider" => 32,
        "Purity" => 40,
        "HolyRelic" => 41,
        "Absolution" => 50,
        "Reaper" => 52,
        "LivingLightning" => 77,
        "HolyArmament" => 78,
        _ => null,
    };

    static bool DarkMonarchDoubles(string family, IReadOnlyDictionary<uint, int> stats) =>
        DarkMonarchSelection(stats) is ushort selected && DarkMonarchMinionFamily(family) == selected;

    static long DarkMonarchLimit(string family, long limit, IReadOnlyDictionary<uint, int> stats) =>
        DarkMonarchDoubles(family, stats) ? limit * 2 : limit;

    static int GolemRosterLimit(int skillBase, IReadOnlyDictionary<uint, int> playerStats)
    {
        bool doubled = DarkMonarchDoubles("Golem", playerStats);
        return GolemCatalog.Limit(skillBase, key => key == Stat.DoubleGolemLimit && doubled
            ? 1 : GolemStat(playerStats, key));
    }
}
