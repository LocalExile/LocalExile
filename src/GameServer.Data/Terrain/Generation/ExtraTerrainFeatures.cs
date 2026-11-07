namespace GameServer.Data.Terrain.Generation;

/// <summary><c>0x14204a570</c>: before the layout, the generator rolls which extra terrain features (league encounters,
/// trials, the discoverable hideout …) the area gets. The rolls draw from the level RNG, so they shift everything after
/// them. Every test reads the area stats the server sent in 0x1d (<see cref="AreaInfo.Stat"/>, 0 without them): with
/// no stats the rolls are those of a plain campaign area; the server's loads carry the 3.29 league stat (22838)
/// on every area, map mods and atlas passives, which this port follows in the client's order (the server corpus,
/// re/011 §8). Not ported here: the plugins' own feature rows (world +0xe0 plugins with byte +10 — Expedition,
/// Ultimatum, Mercenary: the server's plugin state), the generation params' feature list (+0x70) and the Betrayal
/// forts (stat 8609 → <c>0x14204bbf0</c>: the safehouse roster).</summary>
public static class ExtraTerrainFeatures
{
    public sealed record Feature(string Name, int Count);

    /// <summary>The Vaal side area roll (generator +0xa), which the tile graph's side area nodes use.</summary>
    public const string VaalSideArea = "(vaal side area)";

    /// <summary>The discoverable hideout roll (generator +0xb), which the hideout nodes use.</summary>
    public const string DiscoverableHideout = "(discoverable hideout)";

    public static List<Feature> Roll(AreaInfo area, LevelRandom rng)
    {
        var features = new List<Feature>();
        if (area.IsTown || area.RandomnessDisabled || area.AreaType == 0x13 || area.Hash16 == 0x8f53) return features;
        int Stat(int id) => area.Stat(id);
        // The inline percent roll: chance > 0 and (≥ 100 or Next(100) below it); ≤ 0 never draws.
        bool Roll(int chance) => chance > 0 && (chance >= 100 || rng.Next(100u) < (uint)chance);

        // is_blighted_map (10498): seven Blight encounters and nothing else.
        if (Stat(10498) != 0) { features.Add(new("BlightEncounter", 7)); return features; }
        // The terrain plugins with byte +10 (world +0xe0; the server names them in 0x1d's TerrainPluginState): each
        // registers its encounter row through its vtable +0x18. An Expedition logbook area's plugin registers the rows
        // its feature roll picked instead (0x141f362c0, not ported); which MercenaryEncounter* row the Mercenary plugin's
        // blob names is not decoded (every one is a 3 × 3 forced-blank piece with the same list, so any stands in).
        foreach (string plugin in area.Plugins ?? [])
            switch (plugin)
            {
                case "ExpeditionPlugin":
                    if (area.ExpeditionFaction is { } faction) ExpeditionLogbook.Roll(area, rng, faction, features);
                    else features.Add(new("ExpeditionEncounter", 1));
                    break;
                case "UltimatumPlugin": features.Add(new("UltimatumEncounter", 1)); break;
                case "MercenaryPlugin": features.Add(new("MercenaryEncounterWitch1", 1)); break;
            }
        // (the generation params' +0x70 feature list goes here)
        // map_spawn_envoy (15661): 2 = the Tangle, 3 = the Cleansing Fire quest room.
        switch (Stat(15661))
        {
            case 2: features.Add(new("TangleQuest", 1)); break;
            case 3: features.Add(new("CleansingFireQuest", 1)); break;
        }
        // map_nuke_everything (10209): nothing more.
        if (Stat(10209) != 0) return features;
        if (Stat(21265) != 0) features.Add(new("MemoryLineInitiator", 1));
        if (Stat(16965) is var excursions and > 0) features.Add(new("Excursion", excursions));
        // From here every roll reads the area's WorldAreaLeagueChances row (+0x22a; +0x23e with world +0xd7, taken off).
        if (area.LeagueChance is null) return features;
        int Chance(int at) => area.LeagueChance(at);

        // The Deepwater encounters (the 3.29 league; stat 22838 on every area the server runs it in): the entry chance
        // (+180, 100 in every area seen: no draw), then one feature, or two when the second chance (+184) rolls, picked
        // by a partial forward Fisher-Yates over the three rows — one Next(3) whose result is unused (0x14204aba5), then
        // k direct Uniform draws (0x141f5d130), which the corpus's draw tracer never sees.
        if (Stat(22838) != 0 && Stat(8213) == 0 && Stat(20553) == 0 && Roll(Chance(180)))
        {
            var rows = new List<string> { "DeepwaterEncounterGiantCoral", "DeepwaterEncounterCursedTreasure", "DeepwaterEncounterBrinerotStores" };
            int k = Roll(Chance(184)) ? 2 : 1;
            rng.Next((uint)rows.Count);
            for (int i = 0, left = rows.Count; i < k && left > 0; i++, left--)
            {
                int j = (int)rng.Uniform((uint)left);
                (rows[i], rows[i + j]) = (rows[i + j], rows[i]);
            }
            for (int i = 0; i < k; i++) features.Add(new(rows[i], 1));
        }
        if (Stat(10048) != 0) return features;

        // The Vaal side area (generator +0xa): the chance (+96) with the stats 13838 (additive) and 2362 (+%), or stat 5945.
        {
            int chance = Math.Clamp((Stat(2362) + 100) * (Stat(13838) + Chance(96)) / 100, 0, 100);
            if (Stat(5945) != 0 || Roll(chance)) features.Add(new(VaalSideArea, 1));
        }
        if (Stat(3421) != 0) features.Add(new("StoneCircle", 1));
        // The labyrinth trial: forced by stat 4874, else maps at level 73 and up by the chance (+108) plus stat 13839.
        if (Stat(4874) != 0 || (area.IsMapArea && area.Level >= 0x49 && Roll(Stat(13839) + Chance(108))))
            features.Add(new("LabyrinthTrial", 1));
        if (area.AreaType != 35)
        {
            if (Stat(17031) != 0) features.Add(new("HarvestEncounterMemoryLine", 1));
            else if (LeagueCount(area, rng, 18) != 0) features.Add(new("HarvestEncounter", 1));
        }

        // The generator's +0xc: towns, hideout-like and special area types go straight to the fishing mission.
        bool special = area.IsTown || area.RandomnessDisabled || area.AreaType is 0x13 or 0x20 or 0xb || area.Hash16 == 0x7757;
        if (!special)
        {
            // Data/Hideouts.dat: the hideout discoverable here, by its chance in 10 000 (or forced by stat 8705).
            if (area.DiscoverableHideout is { Enabled: true } h && (Stat(8705) != 0 || (h.Chance > 0 && (h.Chance >= 10000 || rng.Next(10000u) < (uint)h.Chance))))
                features.Add(new(DiscoverableHideout, 1));
            // Shrines (generator +0x188 / +0x189): the memory-line stats give the count outright; map_shrines_are_darkshrines
            // (3684) rolls 64 then up to one more at 25 and the league struct's bonus; else the CoreLeagues 1 count.
            int shrines = Math.Max(Stat(17077), Stat(16888));
            if (shrines == 0)
            {
                if (Stat(3684) != 0)
                {
                    var s = LeagueStruct(area, 1);
                    shrines = s.Base;
                    if (Roll(64))
                        for (shrines++; shrines < 2 && Roll(25); shrines++) { }
                    if (s.C2 != 0 && Roll(s.C2)) shrines++;
                }
                else shrines = LeagueCount(area, rng, 1);
            }
            if (shrines != 0) features.Add(new("(shrines)", shrines));
            // betrayal_job_index (8609): -1 = job 1, else one bit per job 1..4 (0x14204bbf0 per job: the BetrayalJobs
            // row's ExtraTerrainFeatures row goes in (LabDefence's BetrayalLaboratory, a row naming the laboratory's
            // world area: its record follows); job 1 picks a fort from the BetrayalForts rows of the area level
            // (+0x8 weight, +0xc..+0x10 the level range, +0x14 the row): Next(total weight), or BetrayalFortFallback
            // with none. The job's world area (+0x51, the safehouse) goes to the generator's level map, not this level.
            int jobs = Stat(8609);
            if (jobs != 0 && area.Table is { } table)
            {
                var jobsDat = table("BetrayalJobs");
                var etf = table("ExtraTerrainFeatures");
                for (int j = 1; j <= 4; j++)
                {
                    if (jobs != -1 && ((jobs >> (j - 1)) & 1) == 0) continue;
                    if (j < jobsDat.Count && jobsDat.Ref(j, 0x10) is var fr and >= 0) features.Add(new(etf.String(fr, 0), 1));
                    if (j == 1)
                    {
                        var forts = table("BetrayalForts");
                        var list = new List<(string Name, int Weight)>();
                        for (int f = 0; f < forts.Count; f++)
                        {
                            int lo = forts.I32(f, 0xc), hi = forts.I32(f, 0x10), weight = forts.I32(f, 8), fort = forts.Ref8(f, 0x14);
                            if (fort >= 0 && lo <= area.Level && area.Level <= hi && weight != 0) list.Add((etf.String(fort, 0), weight));
                        }
                        if (list.Count == 0) features.Add(new("BetrayalFortFallback", 1));
                        else
                        {
                            uint r = rng.Next((uint)list.Sum(e => e.Weight));
                            string pick = list[^1].Name;
                            foreach (var (name, weight) in list)
                            {
                                if (r < (uint)weight) { pick = name; break; }
                                r -= (uint)weight;
                            }
                            features.Add(new(pick, 1));
                        }
                    }
                    if (jobs == -1) break;
                }
            }
            int legion = LeagueCount(area, rng, 0xf);
            if (legion != 0) features.Add(new("LegionMonolith", legion));
            if (LeagueCount(area, rng, 0x10) != 0) features.Add(new("BlightEncounter", 1));
            if (Stat(17912) != 0 && LeagueCount(area, rng, 0x17) != 0) features.Add(new("Crucible", 1));
            int village = Stat(21184) + LeagueCount(area, rng, 0x1b);
            if (village != 0) features.Add(new("Village", village));
            // CoreLeagues 7 (breaches): a hive encounter row, or the generator's +0x18b breach count.
            int breaches = 0;
            bool rolled = false, hive = false;
            if (LeagueCount(area, rng, 7) != 0)
            {
                bool v107 = Stat(22774) != 0 && Stat(22591) == 0;
                bool v108 = !(Stat(22590) == 0 || Stat(22591) != 0);
                hive = Stat(21776) != 0 || v107 || (Stat(5575) == 0 && (Stat(22591) == 0 || Stat(22590) != 0) && (v108 || rng.Next(100u) < 50));
                if (hive)
                {
                    int sky = 1600 * (Stat(22592) + 100) / 100;
                    bool ground = (Stat(22611) == 0 && (area.Level < 0x44 || sky <= 0 || (sky < 10000 && rng.Next(10000u) >= (uint)sky))) || v107;
                    features.Add(new(ground ? "BrequelHiveEncounter" : "BrequelSkyHiveEncounter", 1));
                }
                else { breaches++; rolled = true; }
            }
            if (!hive)
            {
                breaches += Stat(5575);
                if (Stat(5575) != 0 || rolled)
                {
                    if (Roll(Stat(17608))) breaches += 10;
                    if (Roll(Stat(17607))) breaches += 3;
                    if (Roll(Stat(17606))) breaches += 2;
                    if (Roll(Stat(17605))) breaches += 1;
                }
                if (breaches != 0) features.Add(new("(breaches)", breaches));
            }
            // The Abyss areas (by HASH16) with stat 6758 / 6759: two empty rooms.
            if (EmptyRoomAreas.Contains(area.Hash16) && (Stat(6758) != 0 || Stat(6759) != 0))
            {
                features.Add(new("EmptyRoom", 1));
                features.Add(new("EmptyRoom", 1));
            }
        }
        // Every area: the fishing mission, one in 100 000, when the league chances list one (+136..+148) and no stat forbids.
        if (Stat(2360) == 0 && Stat(20139) == 0 && Stat(20140) == 0 && Stat(20142) == 0 && Stat(20143) == 0 &&
            Chance(136) + Chance(140) + Chance(144) + Chance(148) > 0 && rng.Next(100000u) == 0)
            features.Add(new("MissionFish", 1));
        return features;
    }

    static readonly HashSet<ushort> EmptyRoomAreas =
    [
        28018, 25747, 35484, 42556, 5892, 32697, 48844, 14385, 63065, 23253, 8544, 2279, 33025, 47634, 3775, 41751, 26468, 7088,
        39459, 15595, 2099,
    ];

    /// <summary>The CoreLeagues row's chance and follow-up chance columns in the WorldAreaLeagueChances row
    /// (<c>0x141ea1f50</c>'s switch on the row index); a null entry takes stat 16357 alone; a missing row gives nothing.</summary>
    static readonly Dictionary<int, (int Chance, int Follow)?> ChanceColumns = new()
    {
        [0] = (8, 0), [1] = (36, 40), [2] = (96, 0), [3] = (100, 104), [4] = null, [5] = (12, 16), [6] = (56, 60), [7] = (64, 0),
        [8] = null, [9] = (68, 0), [11] = (136, 0), [12] = (140, 0), [13] = (144, 0), [14] = (148, 0), [15] = (44, 0),
        [16] = (72, 0), [17] = (48, 0), [18] = (52, 0), [19] = (76, 0), [20] = (80, 0), [21] = (84, 0), [22] = (92, 0),
        [23] = (112, 0), [24] = (116, 0), [25] = (120, 0), [26] = (152, 0), [27] = (156, 160), [28] = (164, 0),
    };

    public readonly record struct LeagueRoll(int C0, int C1, int C2, int Base, int Max);

    /// <summary><c>0x14204a310</c> (→ <c>0x141ea1f50</c>, <c>0x141ea2290</c>, <c>0x141ea1bb0</c>, <c>0x141ea1c80</c>): the
    /// roll of a CoreLeagues row for this area and its stats: the first chance, the follow-up chance, the bonus chance,
    /// the base count and the most per area. All zero when the league is excluded.</summary>
    public static LeagueRoll LeagueStruct(AreaInfo area, int row)
    {
        var zero = new LeagueRoll(0, 0, 0, 0, 0);
        if (area.CoreLeagues is not { } cl || row >= cl.Count || !ChanceColumns.TryGetValue(row, out var cols)) return zero;
        int Stat(int id) => area.Stat(id);
        // A Stats reference (16 bytes) → the 1-based stat id, 0 for none.
        int Ref(int at) => cl.Ref(row, at) is var r and >= 0 ? r + 1 : 0;
        int[] Refs(int at) => cl.RefList(row, at).Select(r => r + 1).ToArray();
        static int Div(int a, int b) => a / b;   // C truncation, as C#

        // 0x141ea1f50: the chance from the area's league-chances row (scaled by stat 16341, plus 16357), or stat 16357 alone.
        int chance, follow = 0;
        if (cols is null) chance = Stat(16357);
        else
        {
            if (area.LeagueChance is null) return zero;
            chance = Div(area.LeagueChance(cols.Value.Chance) * (Stat(16341) + 100), 100) + Stat(16357);
            follow = cols.Value.Follow != 0 ? area.LeagueChance(cols.Value.Follow) : 0;
        }
        // 0x141ea2290: the exclusions.
        int disable = Ref(10);
        int baseCount = Refs(90).Sum(id => Math.Max(0, Stat(id)));
        int base2Ref = Ref(106);
        int base2 = base2Ref != 0 ? Math.Max(0, Stat(base2Ref)) : 0;
        if ((row < 29 && (Stat(17190) & (1 << row)) != 0) || Stat(10209) != 0 || Stat(20235) != 0 ||
            (!cl.Bool(row, 8) && Stat(8213) != 0) || (!cl.Bool(row, 9) && Stat(10073) != 0)) return zero;
        if (Refs(26).Any(id => Stat(id) != 0)) return zero;
        if (disable != 0 && Stat(disable) != 0 && baseCount + base2 == 0) return zero;
        // 0x141ea1c80: the struct.
        int v10 = cl.Bool(row, 177) ? Stat(20393) : 0;
        if ((cl.Bool(row, 142) && chance + v10 <= 0) || cl.U8(row, 160) == 1) return zero;
        int max = Math.Max(0, cl.I32(row, 138));
        if (max == 0) return zero;
        int bonus = Ref(58);
        int c2 = bonus != 0 ? Math.Clamp(Stat(bonus), 0, 100) : 0;
        var roll = new LeagueRoll(0, 0, c2, baseCount + base2, max);
        if (base2 != 0 || (disable != 0 && Stat(disable) != 0)) return roll;
        int ovr = Ref(143);
        int v26 = ovr != 0 && Stat(ovr) != 0 ? Stat(ovr) : chance + v10;
        int add = Ref(42), pct = Ref(74), fpct = Ref(178);
        int c0 = Math.Clamp(Div((v26 + (add != 0 ? Stat(add) : 0)) * ((pct != 0 ? Stat(pct) : 0) + 100), 100), 0, 100);
        int c1 = Math.Clamp(Div(follow * ((fpct != 0 ? Stat(fpct) : 0) + 100), 100), 0, 100);
        return roll with { C0 = c0, C1 = c1 };
    }

    /// <summary><c>0x141ea2400</c>: how many of a league mechanic the area gets: the base count; one roll at the first
    /// chance, then rolls at the follow-up chance while they succeed, up to the most; then one at the bonus chance. Each
    /// roll is <c>0x142054a50</c>: chance ≥ 100 always, else <c>Next(100)</c> below it.</summary>
    public static int LeagueCount(AreaInfo area, LevelRandom rng, int row)
    {
        var s = LeagueStruct(area, row);
        bool Roll(int c) => c > 0 && (c >= 100 || rng.Next(100u) < (uint)c);
        int n = s.Base;
        if (s.C0 != 0 && Roll(s.C0))
        {
            n++;
            if (s.C1 != 0)
                while (n < s.Max)
                {
                    if (!Roll(s.C1)) break;
                    n++;
                }
        }
        if (s.C2 != 0 && Roll(s.C2)) n++;
        return n;
    }
}
