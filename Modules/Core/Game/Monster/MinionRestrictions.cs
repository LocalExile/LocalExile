public static partial class Game
{
    [Flags]
    enum OwnedMinionFamily
    {
        Golem = 1,
        Spectre = 2,
        Animated = 4,
        Other = 8,
        All = Golem | Spectre | Animated | Other,
    }

    static OwnedMinionFamily AllowedMinionFamilies(IReadOnlyDictionary<uint, int> stats)
    {
        var allowed = OwnedMinionFamily.All;
        if (GolemStat(stats, Stat.YouCannotHaveNonGolemMinions) != 0) allowed &= OwnedMinionFamily.Golem;
        if (GolemStat(stats, Stat.YouCannotHaveNonSpectreMinions) != 0) allowed &= OwnedMinionFamily.Spectre;
        if (GolemStat(stats, Stat.YouCannotHaveNonAnimatedMinions) != 0) allowed &= OwnedMinionFamily.Animated;
        return allowed;
    }

    static OwnedMinionFamily SummonFamily(string family) => family switch
    {
        "Spectre" => OwnedMinionFamily.Spectre,
        "AnimatedWeapon" or "VaalAnimatedWeapon" or "AnimatedGuardian" => OwnedMinionFamily.Animated,
        _ => OwnedMinionFamily.Other,
    };

    static OwnedMinionFamily? SkillMinionFamily(string? effect)
    {
        if (GolemData.Find(effect) is not null) return OwnedMinionFamily.Golem;
        if (effect is "ManifestDancingDervish" or "ManifestDancingDervishUnique") return OwnedMinionFamily.Animated;
        if (SummonData.Find(effect) is { } summon) return SummonFamily(summon.Family);
        return BeastKindFor(effect) is not null ? OwnedMinionFamily.Other : null;
    }

    static bool CanHaveMinion(IReadOnlyDictionary<uint, int> stats, OwnedMinionFamily family) =>
        (AllowedMinionFamilies(stats) & family) != 0;

    static bool CanHaveOtherMinions(Player player) => CanHaveMinion(CharacterPlayerStats(player), OwnedMinionFamily.Other);

    // An equipment restriction retires an owned actor without counting as that minion dying in combat.
    static void RetireDisallowedMinions(Player player, double now)
    {
        if (player.Minions.All.Count == 0 || player.Closed || player.LeavingArea || player.Paused) return;
        var allowed = AllowedMinionFamilies(CharacterPlayerStats(player));
        if (allowed == OwnedMinionFamily.All) return;
        bool saveGolems = false, saveSummons = false;
        using (ProjectileMutation(player))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
            var links = new List<byte>();
            bool endedLinks = false, retired = false;
            foreach (var minion in player.Minions.All.ToArray())
            {
                var family = SkillMinionFamily(minion.Skill) ?? OwnedMinionFamily.Other;
                if ((allowed & family) != 0) continue;
                if (player.Links.On(minion.Id) is { } link)
                {
                    EndLinkLocked(player, link, links);
                    endedLinks = true;
                }
                if (player.Golems.All.GetValueOrDefault(minion.Id) is { } golem)
                {
                    RemoveGolemLocked(player, golem, now);
                    saveGolems = true;
                }
                else if (player.Summons.Fighters.GetValueOrDefault(minion.Id) is { } summon)
                {
                    RemoveSummonLocked(player, summon, now);
                    saveSummons = true;
                }
                else if (player.Reservations.Fighters.GetValueOrDefault(minion.Id) is { } reservation)
                    RemoveReservationFighterLocked(player, reservation, now);
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
            if (endedLinks) LinksChangedLocked(player);
            if (links.Count > 0) player.Minions.OutputQueue.Add(new([.. links], "links to retired minions end"));
            if (retired) player.Minions.StatRevision++;
        }
        if (saveGolems) SaveGolems(player);
        if (saveSummons) SaveSummons(player);
    }
}
