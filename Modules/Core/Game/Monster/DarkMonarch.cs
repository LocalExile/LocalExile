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

    static ushort? DarkMonarchAllowedType(string family) => family switch
    {
        "VaalAbsolution" => 50,
        "VaalDomination" => 1,
        _ => DarkMonarchMinionFamily(family),
    };

    static bool DarkMonarchAllows(string family, IReadOnlyDictionary<uint, int> stats) =>
        DarkMonarchSelection(stats) is not ushort selected || DarkMonarchAllowedType(family) == selected;

    static string? DarkMonarchCastFamily(string? effect)
    {
        if (GolemData.Find(effect) is { IsGolem: true }) return "Golem";
        if (SummonData.Find(effect) is { } summon) return summon.Family;
        if (BeastKindFor(effect) is not null) return "Beast";
        return effect == "SummonSkitterbots" ? "SkitterCold" : null;
    }

    static void RetireDarkMonarchMinions(Player player, double now)
    {
        if (player.Minions.All.Count == 0 || player.Closed || player.LeavingArea || player.Paused
            || DarkMonarchSelection(StatSourceValues(player)) is not ushort selected) return;
        bool saveGolems = false, saveSummons = false, retired = false;
        var links = new List<byte>();
        using (ProjectileMutation(player))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
            foreach (var minion in player.Minions.All.ToArray())
            {
                var fighter = OwnedFighter(player, minion.Id);
                if (DarkMonarchAllowedType(fighter?.Definition.IsGolem == true ? "Golem" : minion.Kind) == selected) continue;
                if (player.Links.On(minion.Id) is { } link) EndLinkLocked(player, link, links);
                if (player.Golems.All.ContainsKey(minion.Id) && fighter is not null)
                {
                    RemoveGolemLocked(player, fighter, now);
                    saveGolems = true;
                }
                else if (player.Summons.Fighters.ContainsKey(minion.Id) && fighter is not null)
                {
                    RemoveSummonLocked(player, fighter, now);
                    saveSummons = true;
                }
                else if (player.Reservations.Fighters.ContainsKey(minion.Id) && fighter is not null)
                    RemoveReservationFighterLocked(player, fighter, now);
                else if (player.Beasts.Active is { } beast && beast.Id == minion.Id)
                    RetireBeastCompanionLocked(player, beast, now, output: true);
                else
                {
                    DropZones(player, minion.Id, MinionAuraZones);
                    if (minion.OwnerBuff != 0) player.Area.Buffs.ClearSource(player.Id, minion.Id);
                    RetireOwnedMinionLocked(player, minion, now);
                }
                retired = true;
            }
            if (links.Count != 0)
            {
                LinksChangedLocked(player);
                player.Minions.OutputQueue.Add(new([.. links], "links to retired minions end"));
            }
            if (retired) player.Minions.StatRevision++;
        }
        if (saveGolems) SaveGolems(player);
        if (saveSummons) SaveSummons(player);
    }

    static int GolemRosterLimit(int skillBase, IReadOnlyDictionary<uint, int> playerStats)
    {
        bool doubled = DarkMonarchDoubles("Golem", playerStats);
        return GolemCatalog.Limit(skillBase, key => key == Stat.DoubleGolemLimit && doubled
            ? 1 : GolemStat(playerStats, key));
    }
}
