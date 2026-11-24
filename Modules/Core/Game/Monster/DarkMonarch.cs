public static partial class Game
{
    static string? DarkMonarchSelection(IReadOnlyDictionary<uint, int> stats) =>
        DarkMonarchMinionFamily(GolemStat(stats, Stat.MaximumNumberOfXMinionTypeDoubled));

    static long DarkMonarchLimit(string family, long limit, IReadOnlyDictionary<uint, int> stats) =>
        DarkMonarchSelection(stats) == CanonicalMinionFamily(family) ? limit * 2 : limit;

    static bool DarkMonarchAllows(string family, IReadOnlyDictionary<uint, int> stats) =>
        DarkMonarchSelection(stats) is not { } selected || CanonicalMinionFamily(family) == selected;

    static string? DarkMonarchCastFamily(string? effect)
    {
        if (GolemData.Find(effect) is { IsGolem: true }) return "Golem";
        if (SummonData.Find(effect) is { Family: not "Clone" } summon) return summon.Family;
        if (BeastKindFor(effect) is not null) return "Beast";
        return effect == "SummonSkitterbots" ? "SkitterCold" : null;
    }

    static void RetireDarkMonarchMinions(Player player, double now)
    {
        if (player.Closed || player.LeavingArea || player.Paused
            || DarkMonarchSelection(StatSourceValues(player)) is null) return;
        bool saveGolems = false, saveSummons = false, retired = false, endedLinks = false;
        var links = new List<byte>();
        using (ProjectileMutation(player))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Minions.All.Count == 0
                || DarkMonarchSelection(StatSourceValues(player)) is not { } selected) return;
            foreach (var minion in player.Minions.All.ToArray())
            {
                var fighter = OwnedFighter(player, minion.Id);
                if (CanonicalMinionFamily(fighter?.Definition.IsGolem == true ? "Golem" : minion.Kind) == selected) continue;
                if (player.Links.On(minion.Id) is { } link)
                {
                    EndLinkLocked(player, link, links);
                    endedLinks = true;
                }
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
                    ClearSharedMinionAuras(player, minion.Id);
                    DropZones(player, minion.Id, MinionAuraZones);
                    if (minion.OwnerBuff != 0) player.Area.Buffs.ClearSource(player.Id, minion.Id);
                    RetireOwnedMinionLocked(player, minion, now);
                }
                retired = true;
            }
            if (endedLinks) LinksChangedLocked(player);
            if (links.Count != 0) player.Minions.OutputQueue.Add(new([.. links], "links to retired minions end"));
            if (retired)
            {
                player.Minions.StatRevision++;
                PlayerStatBuffsChanged(player);
            }
        }
        if (saveGolems) SaveGolems(player);
        if (saveSummons) SaveSummons(player);
    }

    internal static int GolemRosterLimit(IReadOnlyDictionary<uint, int> skillStats, IReadOnlyDictionary<uint, int> playerStats) =>
        SupportedMinionLimit(GolemEffectiveLimit(GolemStat(skillStats, Stat.BaseNumberOfGolemsAllowed),
            key => GolemStat(playerStats, key)), skillStats);
}
