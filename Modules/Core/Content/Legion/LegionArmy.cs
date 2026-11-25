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
    /// (0 normal, 1 elite, 2 sergeant, 3 general), standing from FromPct to ToPct of the formation's depth.
    /// Rarities: one MonsterRarity per monster (null in an old save: the fixed mapping by tier). Rewards: one entry per
    /// monster of the squad, its LegionRewardTypeVisuals rows (empty: none); null when the plan has none.
    /// RevivalMs: Endless Domain revival cooldown from LegionRanks.__col6 (0 for map monoliths).</summary>
    public sealed record Squad(int Tier, int Monster, int Count, int FromPct, int ToPct, int[][]? Rewards = null, int[]? Rarities = null, int RevivalMs = 0);

    /// <summary>A chest monster (LegionChestTypes col0) with the Chests rows (standard and Ruthless HardmodeChest) it turns into at release; WarHoard uses the faction's second LegionChestTypes row (the Epic chest).</summary>
    public sealed record ChestPlan(int Monster, int Chest, bool WarHoard, int FromPct, int ToPct, int[]? Rewards = null, int HardmodeChest = -1);

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

    // ---- the rewards. ALL OF THESE ARE GUESSES; the live game's numbers are not known. Rolled only in Plan/Compose. ----

    /// <summary>The balance row with highest MinLevel &lt;= min(level, MaxLevel).</summary>
    static LegionData.Balance? BalanceFor(LegionData data, int level)
    {
        LegionData.Balance? row = null;
        foreach (var b in data.Balances) if (b.MinLevel <= Math.Min(level, MaxLevel) && (row is null || b.MinLevel > row.MinLevel)) row = b;
        return row;
    }

    /// <summary>GUESS: a sergeant carries a reward with the percent chance of the level's LegionBalancePerLevel __col1 (100 at low levels, 50 from level 41; the column's
    /// meaning is unconfirmed and server-only), plus map_legion_sergeants_reward_additional_chance_% as percentage points.</summary>
    static int SergeantRewardChance(LegionData data, int level) => BalanceFor(data, level)?.Cols[0] ?? 50;

    /// <summary>Maximum Alert gauge before Schism triggers under the Timeless Conflict keystone (LegionBalancePerLevel.__col15, 1000).</summary>
    public static int AlertMax(LegionData data, int level) => BalanceFor(data, level)?.AlertMax ?? 1000;

    /// <summary>Alert value contributed by breaking out a member under Timeless Conflict (LegionRanks.__col8: 5/18/40/350, chests 40/80).</summary>
    public static int RankAlertValue(LegionData data, int level, int tier, bool chest, bool hoard)
    {
        if (chest) return hoard ? 80 : 40;
        var rank = data.Ranks.Where(r => r.Col0 == tier && MinLevelOf(data, r.BalanceKey) <= Math.Min(level, MaxLevel))
            .OrderByDescending(r => MinLevelOf(data, r.BalanceKey)).FirstOrDefault();
        if (rank is { Col8: > 0 }) return rank.Col8;
        return tier switch { 3 => 350, 2 => 40, 1 => 18, _ => 5 };
    }
    /// <summary>GUESS: the number of reward types a War Hoard carries (inclusive range); a normal chest carries <see cref="ChestRewards"/>.</summary>
    const int HoardRewardsMin = 2, HoardRewardsMax = 3, ChestRewards = 1;
    /// <summary>LegionRewardTypeVisuals rows that are never rolled: 11 is Essences again (the same icon and symbol as row 10), 17 is Incubators (no longer drop in 3.29).</summary>
    static readonly int[] ExcludedRewardRows = [11, 17];

    /// <summary>Minimum area level for endgame/league reward symbols so campaign monoliths (e.g. The Climb L7) do not
    /// roll symbols whose pools have no drops yet (Maps/Scarabs L68+, Fragments L36+, league pools L30+).</summary>
    static int RewardMinLevel(int row) => row switch
    {
        3 or 13 => 68,       // Maps, Scarabs (maps only)
        12 => 36,            // Fragments (Sacrifice at Dawn/Noon/Dusk start at L36)
        7 or 8 or 9 or 14 => 30, // Abyss, Harbinger, Breach, Fossils
        _ => 1,
    };

    /// <summary>The reward rows that can be rolled at <paramref name="level"/>: every LegionRewardTypeVisuals row except
    /// <see cref="ExcludedRewardRows"/> and endgame symbols above the area's level.</summary>
    static int[] RewardPool(LegionData data, int level) => [.. data.RewardTypes
        .Where(r => !ExcludedRewardRows.Contains(r.Row) && level >= RewardMinLevel(r.Row))
        .Select(r => r.Row)];

    /// <summary>n different reward rows from the pool (fewer when the pool is smaller).</summary>
    static int[] RollRewards(int[] pool, int n, Random rng)
    {
        var left = pool.ToList();
        var picked = new int[Math.Min(n, left.Count)];
        for (int i = 0; i < picked.Length; i++) { int k = rng.Next(left.Count); picked[i] = left[k]; left.RemoveAt(k); }
        return picked;
    }

    /// <summary>Scales a normal or elite squad (tier 0, 1) by <see cref="CampaignArmyScale"/>, rounded, at least 1; sergeants, the general and chests stay.</summary>
    static Squad ScaleCampaign(Squad s) => s.Tier >= 2 ? s : s with { Count = Math.Max(1, (int)Math.Round(s.Count * CampaignArmyScale)) };

    /// <summary>Chooses the two factions and plans both armies for an area of this level. <paramref name="stats"/> are the
    /// area's composed stats (map owner's Atlas + Scarabs + MapMods); <paramref name="rng"/> is the instance's seeded generator.</summary>
    public static ArmyPlan Plan(LegionData data, int level, string areaId, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        var chosen = ChooseFactions(data, level, areaId, stats, rng);
        int sergeantChance = Math.Clamp(SergeantRewardChance(data, level) + stats.GetValueOrDefault(Stat.MapLegionSergeantsRewardAdditionalChancePct), 0, 100);
        bool[] generals = RollGenerals(data, chosen, level, stats, rng);
        var plans = new FactionPlan[chosen.Count];
        for (int i = 0; i < plans.Length; i++) plans[i] = Compose(data, chosen[i], level, generals[i], stats, rng, sergeantChance);
        if (Campaign.ContainsKey(areaId)) for (int i = 0; i < plans.Length; i++) plans[i] = plans[i] with { Squads = [.. plans[i].Squads.Select(ScaleCampaign)] };
        return new ArmyPlan(level, plans);
    }

    /// <summary>Plans the Endless Domain of Timeless Conflict armies for 2..5 slotted factions using the MinLevel=101
    /// endless rank rows (LegionRanks 56..59, revival cooldowns 6s/6s/16s/24s, *Endless varieties for Tier 1).</summary>
    public static ArmyPlan PlanEndless(LegionData data, IReadOnlyList<string> factionIds, int level, int packSizePct, int uberRewardPct, Random rng)
    {
        var endlessRanks = data.Ranks.Where(r => MinLevelOf(data, r.BalanceKey) > MaxLevel).ToDictionary(r => r.Row);
        var pool = RewardPool(data, level);
        // Scale tier 0..1 wave sizes across 2..5 simultaneous factions so the arena stays lively without starving frames;
        // every faction always keeps all 4 Sergeants and its 1 General.
        double waveScale = factionIds.Count switch { <= 2 => 0.55, 3 => 0.42, 4 => 0.34, _ => 0.28 };
        double packScale = Math.Max(0.25, (100 + packSizePct) / 100.0);
        var plans = new List<FactionPlan>(factionIds.Count);
        foreach (string fid in factionIds)
        {
            if (data.Factions.FirstOrDefault(f => f.Id == fid) is not { } faction) continue;
            var squads = new List<Squad>();
            foreach (var count in data.MonsterCounts.Where(c => c.Faction == faction.Row && endlessRanks.ContainsKey(c.RankKey))
                .OrderBy(c => endlessRanks[c.RankKey].Col0))
            {
                var rank = endlessRanks[count.RankKey];
                int tier = rank.Col0;
                int raw = rng.Next(count.Col2, Math.Max(count.Col2, count.Col3) + 1);
                int n = tier <= 1 ? Math.Max(2, (int)Math.Round(raw * waveScale * packScale)) : raw;
                // Tier 1 has dedicated *Endless varieties on the MinLevel=101 rank row; Tiers 0, 2 and 3 reuse the faction's standard varieties.
                var varieties = data.MonsterVarieties.Where(v => v.Faction == faction.Row && v.RankKey == rank.Row).ToList();
                if (varieties.Count == 0)
                    varieties = data.MonsterVarieties.Where(v => v.Faction == faction.Row && v.RankKey >= 0 && v.RankKey < data.Ranks.Count
                        && data.Ranks[v.RankKey].Col0 == tier && MinLevelOf(data, data.Ranks[v.RankKey].BalanceKey) <= MaxLevel).ToList();
                if (n <= 0 || varieties.Count == 0) continue;
                var per = new int[varieties.Count];
                int totalWeight = varieties.Sum(v => Math.Max(1, v.SpawnWeight));
                for (int i = 0; i < n; i++)
                {
                    int roll = rng.Next(totalWeight), k = 0;
                    while (k < varieties.Count - 1 && (roll -= Math.Max(1, varieties[k].SpawnWeight)) >= 0) k++;
                    per[k]++;
                }
                for (int i = 0; i < per.Length; i++)
                {
                    if (per[i] <= 0) continue;
                    int[][]? rewards = null;
                    if (pool.Length > 0 && tier >= 2)
                    {
                        rewards = new int[per[i]][];
                        for (int j = 0; j < per[i]; j++)
                        {
                            int rCount = tier == 3 ? 2 : 1;
                            if (uberRewardPct > 0 && rng.Next(100) < Math.Clamp(uberRewardPct, 0, 100)) rCount++;
                            rewards[j] = RollRewards(pool, rCount, rng);
                        }
                    }
                    squads.Add(new Squad(tier, varieties[i].Monster, per[i], count.Col4, count.Col5,
                        Rewards: rewards, Rarities: RollRarities(rank, tier, per[i], rng), RevivalMs: rank.Col6));
                }
            }
            plans.Add(new FactionPlan(faction.Row, faction.Id, [.. squads], []));
        }
        return new ArmyPlan(level, [.. plans]);
    }

    static int MinLevelOf(LegionData data, int balanceKey) =>
        balanceKey >= 0 && balanceKey < data.Balances.Count ? data.Balances[balanceKey].MinLevel : int.MaxValue;

    /// <summary>Weight = SpawnWeight x (100 + the faction's map_legion_monoliths_chance_for_..._+%) / 100, among the factions
    /// whose own balance row (a minimum area level: Vaal 15, Templar 46, Maraketh 68) allows the level.
    /// Honors Legion Scarab of The Sekhema (<c>map_legion_contains_maraketh_faction</c>) and Faridun replacement.</summary>
    static List<LegionData.Faction> ChooseFactions(LegionData data, int level, string areaId, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        var chosen = new List<LegionData.Faction>(2);
        var pool = data.Factions.Where(f => f.SpawnWeight > 0 && MinLevelOf(data, f.BalanceKey) <= level).ToList();
        if (Campaign.TryGetValue(areaId, out var fixedIds))
            foreach (string id in fixedIds)
                if (data.Factions.FirstOrDefault(f => f.Id == id) is { } f && chosen.Count < 2) { chosen.Add(f); pool.Remove(f); }
        bool forceMaraketh = stats.GetValueOrDefault(Stat.MapLegionContainsMarakethFaction) > 0;
        bool faridunReplace = stats.GetValueOrDefault(Stat.MapLegionFaridunReplaceMarakethFaction) > 0;
        int marakethWeight = data.Factions.FirstOrDefault(f => f.Id == "Maraketh")?.SpawnWeight ?? 220;
        if (forceMaraketh && chosen.Count < 2)
        {
            string targetId = faridunReplace ? "Faridun" : "Maraketh";
            var forced = data.Factions.FirstOrDefault(f => f.Id == targetId)
                ?? data.Factions.FirstOrDefault(f => f.Id == "Maraketh");
            if (forced is not null && !chosen.Contains(forced))
            {
                chosen.Add(forced);
                pool.RemoveAll(f => f.Id is "Maraketh" or "Faridun");
            }
        }
        else if (faridunReplace && pool.FirstOrDefault(f => f.Id == "Maraketh") is { } mkt
            && data.Factions.FirstOrDefault(f => f.Id == "Faridun") is { } faridun)
        {
            int idx = pool.IndexOf(mkt);
            if (idx >= 0) pool[idx] = faridun;
        }
        while (chosen.Count < 2 && pool.Count > 0)
        {
            // The stat's id is its Stats row + 1 (the Stat.g.cs ids); LegionFactions.StatsKey is the row.
            var weights = pool.Select(f =>
            {
                int baseWeight = f.SpawnWeight > 0 ? f.SpawnWeight : marakethWeight;
                return baseWeight * Math.Max(0, 100 + (f.StatsKey >= 0 ? stats.GetValueOrDefault((uint)f.StatsKey + 1) : 0)) / 100.0;
            }).ToArray();
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

    /// <summary>Flags which armies bring a General: honors <c>map_legion_general_chance_+%</c>, legacy
    /// <c>map_legion_league_force_general</c> and Face Off (<c>map_legion_generals_spawn_both_generals</c>).</summary>
    static bool[] RollGenerals(LegionData data, List<LegionData.Faction> chosen, int level, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        var result = new bool[chosen.Count];
        if (chosen.Count == 0 || FindGroup(data, chosen[0], level) is not { } rows) return result;
        int basePct = rows.Values.FirstOrDefault(r => r.Col0 == 3)?.Col7 ?? 0;
        long pct = (long)basePct * Math.Max(0, 100 + stats.GetValueOrDefault(Stat.MapLegionGeneralChancePlusPct)) / 100;
        bool spawned = stats.GetValueOrDefault(Stat.MapLegionLeagueForceGeneral) > 0 || rng.Next(100) < pct;
        if (!spawned) return result;
        if (stats.GetValueOrDefault(Stat.MapLegionGeneralsSpawnBothGenerals) > 0)
            Array.Fill(result, true);
        else
            result[rng.Next(chosen.Count)] = true;
        return result;
    }

    static int[] RollMonsterRewards(int[] pool, int addOnePct, int addTwoPct, Random rng)
    {
        int count = 1;
        if (addTwoPct > 0 && rng.Next(100) < addTwoPct) count += 2;
        else if (addOnePct > 0 && rng.Next(100) < addOnePct) count += 1;
        return RollRewards(pool, count, rng);
    }

    static FactionPlan Compose(LegionData data, LegionData.Faction faction, int level, bool general,
        IReadOnlyDictionary<uint, int> stats, Random rng, int sergeantChance)
    {
        int extraSergeants = Math.Max(0, stats.GetValueOrDefault(Stat.MapLegionAdditionalNumberOfSergeants));
        int packSizePct = stats.GetValueOrDefault(Stat.MapLegionPackSizePlusPct);
        int extraChestRewards = Math.Max(0, stats.GetValueOrDefault(Stat.MapLegionChestsContainsAnAdditionalReward));
        int addOneRewardPct = Math.Clamp(stats.GetValueOrDefault(Stat.MapLegionMonstersWithRewardsHavePctChanceToGainAdditionalReward), 0, 100);
        int addTwoRewardsPct = Math.Clamp(stats.GetValueOrDefault(Stat.MapLegionMonstersWithRewardsHavePctChanceToGainTwoAdditionalRewards), 0, 100);
        int monsterRewardPct = stats.GetValueOrDefault(Stat.MapLegionMonsterRewardChancePlusPct);
        int soldierRewardChance = monsterRewardPct > 0 ? Math.Clamp((int)Math.Round(10 * (100 + monsterRewardPct) / 100.0), 1, 100) : 0;

        var squads = new List<Squad>();
        if (FindGroup(data, faction, level) is { } rows)
            foreach (var count in data.MonsterCounts.Where(c => c.Faction == faction.Row && rows.ContainsKey(c.RankKey)).OrderBy(c => rows[c.RankKey].Col0))
            {
                int tier = rows[count.RankKey].Col0;
                if (tier == 3 && !general) continue;
                int n = rng.Next(count.Col2, Math.Max(count.Col2, count.Col3) + 1);
                if (tier == 2 && extraSergeants > 0) n += extraSergeants;
                else if (tier <= 1 && packSizePct != 0) n = Math.Max(1, (int)Math.Round(n * Math.Max(0.25, (100 + packSizePct) / 100.0)));
                var varieties = data.MonsterVarieties.Where(v => v.Faction == faction.Row && v.RankKey >= 0 && v.RankKey < data.Ranks.Count
                    && data.Ranks[v.RankKey].Col0 == tier && MinLevelOf(data, data.Ranks[v.RankKey].BalanceKey) <= MaxLevel).ToList();
                if (n <= 0 || varieties.Count == 0) continue;
                var per = new int[varieties.Count];
                int totalWeight = varieties.Sum(v => Math.Max(1, v.SpawnWeight));
                for (int i = 0; i < n; i++)
                {
                    int roll = rng.Next(totalWeight), k = 0;
                    while (k < varieties.Count - 1 && (roll -= Math.Max(1, varieties[k].SpawnWeight)) >= 0) k++;
                    per[k]++;
                }
                for (int i = 0; i < per.Length; i++)
                    if (per[i] > 0) squads.Add(new Squad(tier, varieties[i].Monster, per[i], count.Col4, count.Col5, Rarities: RollRarities(rows[count.RankKey], tier, per[i], rng)));
            }
        var pool = RewardPool(data, level);
        for (int i = 0; i < squads.Count; i++)
        {
            if (pool.Length == 0) continue;
            if (squads[i].Tier == 2)   // the general has none: its drops come from its own rolls
                squads[i] = squads[i] with
                {
                    Rewards = [.. Enumerable.Range(0, squads[i].Count).Select(_ =>
                        rng.Next(100) < sergeantChance ? RollMonsterRewards(pool, addOneRewardPct, addTwoRewardsPct, rng) : [])]
                };
            else if (squads[i].Tier <= 1 && soldierRewardChance > 0)
                squads[i] = squads[i] with
                {
                    Rewards = [.. Enumerable.Range(0, squads[i].Count).Select(_ =>
                        rng.Next(100) < soldierRewardChance ? RollMonsterRewards(pool, addOneRewardPct, addTwoRewardsPct, rng) : [])]
                };
        }
        var chests = PlanChests(data, faction, level, stats, rng);
        if (pool.Length > 0)
            for (int i = 0; i < chests.Count; i++)
            {
                int rewardCount = (chests[i].WarHoard ? rng.Next(HoardRewardsMin, HoardRewardsMax + 1) : ChestRewards) + extraChestRewards;
                chests[i] = chests[i] with { Rewards = RollRewards(pool, rewardCount, rng) };
            }
        return new FactionPlan(faction.Row, faction.Id, [.. squads], [.. chests]);
    }

    /// <summary>GUESS: the LegionRanks row's col1..col4 are the weights of Normal, Magic, Rare and Unique for its monsters (soldiers 90/10, elites 75/25, sergeants
    /// Rare, the general Unique); a row with no weights keeps the fixed mapping by tier (<see cref="TierRarity"/>). One roll per monster with the plan's Random.</summary>
    static int[] RollRarities(LegionData.Rank rank, int tier, int count, Random rng)
    {
        int[] weights = [rank.Col1, rank.Col2, rank.Col3, rank.Col4];
        int total = weights.Sum(w => Math.Max(0, w));
        var result = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (total <= 0) { result[i] = TierRarity(tier); continue; }
            int pick = rng.Next(total), k = 0;
            while (k < 3 && (pick -= Math.Max(0, weights[k])) >= 0) k++;
            result[i] = k;
        }
        return result;
    }

    /// <summary>The fixed rarity of a tier (the old mapping, kept for a rank row without weights and for a saved plan without rarities): a MonsterRarity value.</summary>
    public static int TierRarity(int tier) => tier switch { 1 => 1, 2 => 2, 3 => 3, _ => 0 };

    static List<ChestPlan> PlanChests(LegionData data, LegionData.Faction faction, int level, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        var chests = new List<ChestPlan>();
        var rows = data.ChestCounts.Where(c => c.Faction == faction.Row && c.MinLevel <= level).ToList();
        if (rows.Count == 0) return chests;
        int minLevel = rows.Max(c => c.MinLevel);
        rows = [.. rows.Where(c => c.MinLevel == minLevel)];
        LegionData.ChestCount? Tier(int tier) => rows.FirstOrDefault(c => c.RankKey >= 0 && c.RankKey < data.Ranks.Count && data.Ranks[c.RankKey].Col0 == tier);
        if (Tier(0) is not { } main) return chests;
        var hoardRow = Tier(1);
        int hoardPct = hoardRow?.Col7 ?? 0;
        var types = data.ChestTypes.Where(t => t.Faction == faction.Row).ToList();
        if (types.Count == 0) return chests;
        // GUESS: the real game shows chests in nearly every army; the table's col2 is 0, so a roll of 0 would leave an army without one.
        int n = Math.Max(MinChestsPerArmy, rng.Next(main.Col2, Math.Max(main.Col2, main.Col3) + 1));
        bool forceWarChest = stats.GetValueOrDefault(Stat.MapLegionLeagueForceWarChest) > 0;
        bool hoard = forceWarChest || rng.Next(100) < hoardPct;
        int hoardAt = hoard ? rng.Next(n) : -1;
        // A faction has two chest monsters (LegionChestTypes, in row order): the plain chest (MonsterChestKarui1) and the
        // War Hoard (MonsterChestKarui2, the "Epic" one, 3x the life). The War Hoard roll picks the second and uses Tier 1's depth band (35..100%).
        types.Sort((a, b) => a.Row.CompareTo(b.Row));
        for (int i = 0; i < n; i++)
        {
            bool isHoard = hoard && i == hoardAt;
            var type = isHoard && types.Count > 1 ? types[1] : types[0];
            int from = isHoard && hoardRow is not null ? hoardRow.Col4 : main.Col4;
            int to = isHoard && hoardRow is not null ? hoardRow.Col5 : main.Col5;
            chests.Add(new ChestPlan(type.Col0, type.Chest, isHoard, from, to, HardmodeChest: type.HardmodeChest));
        }
        // War Supplies (map_legion_army_additional_war_chest): each point adds an extra War Hoard to the army.
        int extraHoards = Math.Max(0, stats.GetValueOrDefault(Stat.MapLegionArmyAdditionalWarChest));
        var hoardType = types.Count > 1 ? types[1] : types[0];
        int hoardFrom = hoardRow?.Col4 ?? 35, hoardTo = hoardRow?.Col5 ?? 100;
        for (int i = 0; i < extraHoards; i++)
            chests.Add(new ChestPlan(hoardType.Col0, hoardType.Chest, true, hoardFrom, hoardTo, HardmodeChest: hoardType.HardmodeChest));
        return chests;
    }

    /// <summary>The activation's one log line.</summary>
    public static string Summary(ArmyPlan plan)
    {
        int monsters = 0, sergeants = 0, generals = 0, chests = 0, rewards = 0;
        var types = new SortedSet<int>();
        foreach (var f in plan.Factions)
        {
            chests += f.Chests.Length;
            foreach (var c in f.Chests) if (c.Rewards is { } cr) { rewards += cr.Length; types.UnionWith(cr); }
            foreach (var s in f.Squads) if (s.Rewards is { } sr) foreach (var r in sr) { rewards += r.Length; types.UnionWith(r); }
            foreach (var s in f.Squads) { monsters += s.Count; if (s.Tier == 2) sergeants += s.Count; else if (s.Tier == 3) generals += s.Count; }
        }
        return $"factions {string.Join("+", plan.Factions.Select(f => f.Id))}, {monsters} monsters ({sergeants} sergeants, {generals} general), {chests} chests, {rewards} rewards (types {string.Join(",", types)})";
    }
}
