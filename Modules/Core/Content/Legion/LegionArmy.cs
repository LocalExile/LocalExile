using GameServer.Core;

namespace GameServer.Modules.Legion;

/// <summary>
/// Plans the two armies of a Legion encounter: which two factions fight and how many monsters, sergeants, generals and
/// chests each brings, with the distance band (percent of the formation's depth) each group stands in. Pure planning, no
/// spawning: the army tasks that follow read the <see cref="ArmyPlan"/> kept with the encounter.
/// </summary>
static class LegionArmy
{
    /// <summary>A group of monsters of one variety (LegionMonsterVarieties.MonsterVarietiesKey) of one rank tier
    /// (0 normal, 1 elite, 2 sergeant, 3 general), standing from FromPct to ToPct of the formation's depth.</summary>
    public sealed record Squad(int Tier, int Monster, int Count, int FromPct, int ToPct);

    /// <summary>A chest monster (LegionChestTypes col0) with the Chests row it turns into; WarHoard uses the faction's second LegionChestTypes row (the Epic chest).</summary>
    public sealed record ChestPlan(int Monster, int Chest, bool WarHoard, int FromPct, int ToPct);

    public sealed record FactionPlan(int Faction, string Id, Squad[] Squads, ChestPlan[] Chests);

    public sealed record ArmyPlan(int Level, FactionPlan[] Factions);

    /// <summary>The highest area level the encounter is planned for; balance rows above it (MinLevel 101) are the endless ones.</summary>
    const int MaxLevel = 100;

    /// <summary>The campaign areas whose monolith always has the same factions; the rest are drawn at random.
    /// Wiki: The Climb (Act 1) Eternal Empire + Karui, The Dried Lake (Act 4) Eternal Empire and one random other.</summary>
    static readonly Dictionary<string, string[]> Campaign = new()
    {
        ["1_1_6"] = ["EternalEmpire", "Karui"],
        ["1_4_2"] = ["EternalEmpire"],
    };

    /// <summary>The share of the planned monsters and elites that the campaign's fixed monoliths bring. A GUESS from the user's memory (the
    /// campaign encounter looked smaller than the maps'); tune after live check.</summary>
    const double CampaignArmyScale = 0.5;

    /// <summary>The fewest chests an army has (GUESS, see PlanChests); chests are never scaled by <see cref="CampaignArmyScale"/>.</summary>
    const int MinChestsPerArmy = 1;

    /// <summary>Scales a normal or elite squad (tier 0, 1) by <see cref="CampaignArmyScale"/>, rounded, at least 1; sergeants, the general and chests stay.</summary>
    static Squad ScaleCampaign(Squad s) => s.Tier >= 2 ? s : s with { Count = Math.Max(1, (int)Math.Round(s.Count * CampaignArmyScale)) };

    /// <summary>Chooses the two factions and plans both armies for an area of this level. <paramref name="stats"/> are the
    /// area's league stats (Game.AreaLeagueStats); <paramref name="rng"/> is the instance's seeded generator.</summary>
    public static ArmyPlan Plan(LegionData data, int level, string areaId, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        var chosen = ChooseFactions(data, level, areaId, stats, rng);
        int general = RollGeneral(data, chosen, level, stats, rng);
        var plans = new FactionPlan[chosen.Count];
        for (int i = 0; i < plans.Length; i++) plans[i] = Compose(data, chosen[i], level, i == general, rng);
        if (Campaign.ContainsKey(areaId)) for (int i = 0; i < plans.Length; i++) plans[i] = plans[i] with { Squads = [.. plans[i].Squads.Select(ScaleCampaign)] };
        return new ArmyPlan(level, plans);
    }

    static int MinLevelOf(LegionData data, int balanceKey) =>
        balanceKey >= 0 && balanceKey < data.Balances.Count ? data.Balances[balanceKey].MinLevel : int.MaxValue;

    /// <summary>Weight = SpawnWeight x (100 + the faction's map_legion_monoliths_chance_for_..._+%) / 100, among the factions
    /// whose own balance row (a minimum area level: Vaal 15, Templar 46, Maraketh 68) allows the level.</summary>
    static List<LegionData.Faction> ChooseFactions(LegionData data, int level, string areaId, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        var chosen = new List<LegionData.Faction>(2);
        var pool = data.Factions.Where(f => f.SpawnWeight > 0 && MinLevelOf(data, f.BalanceKey) <= level).ToList();
        if (Campaign.TryGetValue(areaId, out var fixedIds))
            foreach (string id in fixedIds)
                if (data.Factions.FirstOrDefault(f => f.Id == id) is { } f && chosen.Count < 2) { chosen.Add(f); pool.Remove(f); }
        while (chosen.Count < 2 && pool.Count > 0)
        {
            // The stat's id is its Stats row + 1 (the Stat.g.cs ids); LegionFactions.StatsKey is the row.
            var weights = pool.Select(f => f.SpawnWeight * Math.Max(0, 100 + (f.StatsKey >= 0 ? stats.GetValueOrDefault((uint)f.StatsKey + 1) : 0)) / 100.0).ToArray();
            double total = weights.Sum(), roll = rng.NextDouble() * total;
            int pick = 0;
            if (total > 0) { while (pick < weights.Length - 1 && (roll -= weights[pick]) >= 0) pick++; }
            else pick = rng.Next(pool.Count);
            chosen.Add(pool[pick]);
            pool.RemoveAt(pick);
        }
        return chosen;
    }

    /// <summary>The rank rows (by row) of the group with the highest MinLevel &lt;= level that has monster counts for the faction.</summary>
    static Dictionary<int, LegionData.Rank>? FindGroup(LegionData data, LegionData.Faction faction, int level)
    {
        var groups = data.Ranks.Where(r => MinLevelOf(data, r.BalanceKey) <= Math.Min(level, MaxLevel))
            .GroupBy(r => r.BalanceKey).OrderByDescending(g => MinLevelOf(data, g.Key));
        foreach (var group in groups)
        {
            var rows = group.ToDictionary(r => r.Row);
            if (data.MonsterCounts.Any(c => c.Faction == faction.Row && rows.ContainsKey(c.RankKey))) return rows;
        }
        return null;
    }

    /// <summary>Index of the army that brings the encounter's one general, or -1. TODO phase 4: map_legion_generals_spawn_both_generals.</summary>
    static int RollGeneral(LegionData data, List<LegionData.Faction> chosen, int level, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        if (chosen.Count == 0 || FindGroup(data, chosen[0], level) is not { } rows) return -1;
        int basePct = rows.Values.FirstOrDefault(r => r.Col0 == 3)?.Col7 ?? 0;
        long pct = (long)basePct * Math.Max(0, 100 + stats.GetValueOrDefault(Stat.MapLegionGeneralChancePlusPct)) / 100;
        return rng.Next(100) < pct ? rng.Next(chosen.Count) : -1;
    }

    /// <summary>
    /// THE COUNT MODEL AND THE GENERAL AND CHEST ROLLS, A GUESS FROM THE DATA; reverse engineering can correct them here
    /// and nowhere else. Per faction and area level:
    /// - the rank group is the LegionRanks rows (one per tier 0..3, tier = col0) of the balance row with the highest
    ///   MinLevel &lt;= level, falling back to a lower group that has LegionMonsterCounts for the faction (ranks 12-15 have none);
    /// - LegionMonsterCounts per rank row: col2..col3 = the min and max count (inclusive), col4..col5 = the band of the
    ///   formation's depth in percent where the group stands, col6 = the number of varieties (matches LegionMonsterVarieties);
    ///   tier 0 and 1 are the normal and elite monsters, tier 2 sergeants, tier 3 the general (counted only when it appears);
    /// - the general: the chosen rank group's tier 3 row col7 is the base percent chance (0 below level 46, 1 at 46, up to 8
    ///   at 82; the endless row 0) of one general in the whole encounter, times (100 + map_legion_general_chance_+%) / 100,
    ///   and it goes to one of the two armies at random (at most one);
    /// - varieties are the faction's LegionMonsterVarieties rows of the same tier (those exist only for rank rows 0..3; rows
    ///   whose rank is endless, MinLevel &gt; 100, are left out), each monster picking one uniformly;
    /// - LegionChestCounts: the faction's rows with the highest MinLevel &lt;= level; the tier 0 row gives col2..col3 chests
    ///   (0 to 2, no forced minimum) and the band col4..col5; the tier 1 row's col7 is the percent chance, rolled once per army,
    ///   that one of its chests (or one extra if it has none) is a War Hoard (the faction's second LegionChestTypes row). The other reading, a roll per chest,
    ///   gives more hoards; the chest's type is one of the faction's two LegionChestTypes.
    /// </summary>
    static FactionPlan Compose(LegionData data, LegionData.Faction faction, int level, bool general, Random rng)
    {
        var squads = new List<Squad>();
        if (FindGroup(data, faction, level) is { } rows)
            foreach (var count in data.MonsterCounts.Where(c => c.Faction == faction.Row && rows.ContainsKey(c.RankKey)).OrderBy(c => rows[c.RankKey].Col0))
            {
                int tier = rows[count.RankKey].Col0;
                if (tier == 3 && !general) continue;
                int n = rng.Next(count.Col2, Math.Max(count.Col2, count.Col3) + 1);
                var varieties = data.MonsterVarieties.Where(v => v.Faction == faction.Row && v.RankKey >= 0 && v.RankKey < data.Ranks.Count
                    && data.Ranks[v.RankKey].Col0 == tier && MinLevelOf(data, data.Ranks[v.RankKey].BalanceKey) <= MaxLevel).ToList();
                if (n <= 0 || varieties.Count == 0) continue;
                var per = new int[varieties.Count];
                for (int i = 0; i < n; i++) per[rng.Next(per.Length)]++;
                for (int i = 0; i < per.Length; i++)
                    if (per[i] > 0) squads.Add(new Squad(tier, varieties[i].Monster, per[i], count.Col4, count.Col5));
            }
        return new FactionPlan(faction.Row, faction.Id, [.. squads], [.. PlanChests(data, faction, level, rng)]);
    }

    static List<ChestPlan> PlanChests(LegionData data, LegionData.Faction faction, int level, Random rng)
    {
        var chests = new List<ChestPlan>();
        var rows = data.ChestCounts.Where(c => c.Faction == faction.Row && c.MinLevel <= level).ToList();
        if (rows.Count == 0) return chests;
        int minLevel = rows.Max(c => c.MinLevel);
        rows = [.. rows.Where(c => c.MinLevel == minLevel)];
        LegionData.ChestCount? Tier(int tier) => rows.FirstOrDefault(c => c.RankKey >= 0 && c.RankKey < data.Ranks.Count && data.Ranks[c.RankKey].Col0 == tier);
        if (Tier(0) is not { } main) return chests;
        int hoardPct = Tier(1)?.Col7 ?? 0;
        var types = data.ChestTypes.Where(t => t.Faction == faction.Row).ToList();
        if (types.Count == 0) return chests;
        // GUESS: the real game shows chests in nearly every army; the table's col2 is 0, so a roll of 0 would leave an army without one.
        int n = Math.Max(MinChestsPerArmy, rng.Next(main.Col2, Math.Max(main.Col2, main.Col3) + 1));
        bool hoard = rng.Next(100) < hoardPct;
        int hoardAt = hoard && n > 0 ? rng.Next(n) : 0;
        if (hoard && n == 0) n = 1;
        // A faction has two chest monsters (LegionChestTypes, in row order): the plain chest (MonsterChestKarui1) and the
        // War Hoard (MonsterChestKarui2, the "Epic" one, 3x the life). The War Hoard roll picks the second.
        types.Sort((a, b) => a.Row.CompareTo(b.Row));
        for (int i = 0; i < n; i++)
        {
            bool isHoard = hoard && i == hoardAt;
            var type = isHoard && types.Count > 1 ? types[1] : types[0];
            chests.Add(new ChestPlan(type.Col0, type.Chest, isHoard, main.Col4, main.Col5));
        }
        return chests;
    }

    /// <summary>The activation's one log line.</summary>
    public static string Summary(ArmyPlan plan)
    {
        int monsters = 0, sergeants = 0, generals = 0, chests = 0;
        foreach (var f in plan.Factions)
        {
            chests += f.Chests.Length;
            foreach (var s in f.Squads) { monsters += s.Count; if (s.Tier == 2) sergeants += s.Count; else if (s.Tier == 3) generals += s.Count; }
        }
        return $"factions {string.Join("+", plan.Factions.Select(f => f.Id))}, {monsters} monsters ({sergeants} sergeants, {generals} general), {chests} chests";
    }
}
