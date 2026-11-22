namespace GameServer.Modules.Harvest;

/// <summary>
/// The Sacred Grove's crop roll, with no state of its own (every roll takes the caller's <see cref="Random"/>): how many
/// pairs of crops, each crop's colour, and its 23 seeds by tier and species (HarvestSeeds, HarvestPerLevelValues); in a
/// memory-line grove, single crops of 45-47 seeds.
/// </summary>
public static class GroveCrops
{
    public const int SeedsPerCrop = 23;
    /// <summary>A memory-line crop's seed count, uniform in this range (the wiki's 45-47 "depending on location").</summary>
    public const int MemoryLineSeedsMin = 45, MemoryLineSeedsMax = 47;

    /// <summary>One seed row of a crop and how many of it.</summary>
    public sealed record SeedCount(int Row, int Count);

    /// <summary>A crop: its site (the pair it belongs to), its slot in the pair (0 or 1), its colour and its seeds.</summary>
    public sealed record Crop(int Site, int Slot, int Colour, IReadOnlyList<SeedCount> Seeds);

    /// <summary>The number of crop pairs: 3 or 4 at equal odds, one more at the additional plot chance, plus the flat
    /// additional plots, at most <paramref name="sites"/>.</summary>
    public static int Pairs(Random random, HarvestStats stats, int sites)
    {
        int pairs = 3 + random.Next(2);
        if (random.Next(100) < stats.AdditionalPlotChance) pairs++;
        pairs += Math.Max(0, stats.AdditionalPlots);
        return Math.Clamp(pairs, 0, sites);
    }

    /// <summary>The two colours of a pair. Each crop's colour is weighted 1 × (1 + its plot chance / 100), at least 0;
    /// with every weight at 0 both crops take one random colour; the 1-of-every-2 override makes one crop that colour.</summary>
    public static (int A, int B) PairColours(Random random, HarvestStats stats)
    {
        double[] weights =
        [
            Math.Max(0, 1 + stats.RedWeight / 100.0),
            Math.Max(0, 1 + stats.GreenWeight / 100.0),
            Math.Max(0, 1 + stats.BlueWeight / 100.0),
        ];
        double total = weights.Sum();
        int Pick()
        {
            double roll = random.NextDouble() * total;
            for (int c = 0; c < 3; c++)
                if ((roll -= weights[c]) < 0 && weights[c] > 0) return c + 1;
            return Array.FindLastIndex(weights, w => w > 0) + 1;
        }
        int a, b;
        if (total <= 0) a = b = 1 + random.Next(3);
        else { a = Pick(); b = Pick(); }
        if (stats.ColourOverride is >= 1 and <= 3 && a != stats.ColourOverride && b != stats.ColourOverride)
        {
            if (random.Next(2) == 0) a = stats.ColourOverride; else b = stats.ColourOverride;
        }
        return (a, b);
    }

    static int Binomial(Random random, int trials, double chance)
    {
        int hits = 0;
        for (int i = 0; i < trials; i++) if (random.NextDouble() < chance) hits++;
        return hits;
    }

    /// <summary>The tier of each of a crop's 23 seeds (4, 3, 2, 1 counts): Tier 4 at the level's chance × (1 + the
    /// boss encounter increase) plus the flat chance; Tier 3 as Binom(rolls, chance × (1 + the T3 increase)); Tier 2 as
    /// Binom(rolls, chance); the rest Tier 1. At-least-Tier-2 and the upgrade chances apply after. Under Crop Rotation
    /// (outside memory-line groves) every seed starts at Tier 1. A larger crop (<paramref name="seeds"/>) scales the
    /// Tier 3 and Tier 2 roll counts with it (a guess).</summary>
    public static int[] Tiers(Random random, HarvestTables.LevelValues level, HarvestStats stats, int seeds = SeedsPerCrop)
    {
        if (stats.CropRotation) return [0, seeds, 0, 0, 0];
        var tiers = new int[5];
        int Rolls(int rolls) => seeds == SeedsPerCrop ? rolls : (int)Math.Round((double)rolls * seeds / SeedsPerCrop);
        double t4 = level.Tier4 / 10000.0 * Math.Max(0, 1 + stats.Tier4ChanceIncrease / 100.0) + stats.Tier4FlatChance / 100.0;
        tiers[4] = random.NextDouble() < t4 ? 1 : 0;
        tiers[3] = Binomial(random, Rolls(level.Tier3Rolls), level.Tier3Chance / 10000.0 * Math.Max(0, 1 + stats.Tier3ChanceIncrease / 100.0));
        tiers[2] = Binomial(random, Rolls(level.Tier2Rolls), level.Tier2Chance / 10000.0);
        tiers[3] = Math.Min(tiers[3], seeds - tiers[4]);
        tiers[2] = Math.Min(tiers[2], seeds - tiers[4] - tiers[3]);
        tiers[1] = seeds - tiers[4] - tiers[3] - tiers[2];
        if (stats.AtLeastTier2) { tiers[2] += tiers[1]; tiers[1] = 0; }
        if (stats.Tier2UpgradeChance > 0 || stats.Tier3UpgradeChance > 0)
        {
            int up2 = Binomial(random, tiers[1], stats.Tier2UpgradeChance / 100.0);
            int up3 = Binomial(random, tiers[2], stats.Tier3UpgradeChance / 100.0);
            tiers[1] -= up2; tiers[2] += up2 - up3; tiers[3] += up3;
        }
        return tiers;
    }

    /// <summary>A crop's seeds for tier counts: one species per tier, uniform among the colour's species of that tier,
    /// Tier 1 split between two species.</summary>
    public static List<SeedCount> Species(Random random, HarvestTables tables, int colour, int[] tiers)
    {
        var seeds = new List<SeedCount>();
        for (int tier = 4; tier >= 1; tier--) AddTier(random, tables, seeds, colour, tier, tiers[tier]);
        return seeds;
    }

    static void AddTier(Random random, HarvestTables tables, List<SeedCount> seeds, int colour, int tier, int count)
    {
        if (count == 0 || tables.ByColourTier[colour][tier] is not { Length: > 0 } rows) return;
        if (tier == 1 && rows.Length > 1 && count > 1)
        {
            int a = random.Next(rows.Length), b = (a + 1 + random.Next(rows.Length - 1)) % rows.Length;
            int first = 0;
            for (int i = 0; i < count; i++) if (random.Next(2) == 0) first++;
            if (first > 0) seeds.Add(new(rows[a], first));
            if (count - first > 0) seeds.Add(new(rows[b], count - first));
        }
        else seeds.Add(new(rows[random.Next(rows.Length)], count));
    }

    /// <summary>A mixed-colour crop's seeds (map_harvest_mixed_seed_colours): each seed of a tier takes one of the three
    /// colours at equal odds, then each colour's share of the tier is chosen as <see cref="Species"/> does (a guess).</summary>
    public static List<SeedCount> MixedSpecies(Random random, HarvestTables tables, int[] tiers)
    {
        var seeds = new List<SeedCount>();
        for (int tier = 4; tier >= 1; tier--)
        {
            var byColour = new int[4];
            for (int i = 0; i < tiers[tier]; i++) byColour[1 + random.Next(3)]++;
            for (int colour = 1; colour <= 3; colour++) AddTier(random, tables, seeds, colour, tier, byColour[colour]);
        }
        return seeds;
    }

    /// <summary>The colour most of a crop's seeds have, the lowest on a tie: a mixed crop's Extractor, Irrigator, Pole and
    /// Storage Tank show it (a guess).</summary>
    public static int Majority(HarvestTables tables, IReadOnlyList<SeedCount> seeds, int fallback)
    {
        var count = new int[4];
        foreach (var s in seeds)
            if (tables.SeedRow(s.Row) is { Colour: >= 1 and <= 3 } seed) count[seed.Colour] += s.Count;
        int best = 0;
        for (int c = 1; c <= 3; c++) if (count[c] > count[best]) best = c;
        return best == 0 ? fallback : best;
    }

    /// <summary>The grove's crops: the pairs on the first sites of a shuffled order, two crops each, and with
    /// map_harvest_always_high_tier_seeds one more Tier 4 seed of each colour, in a crop of that colour without one.</summary>
    public static List<Crop> Roll(Random random, HarvestTables tables, HarvestTables.LevelValues level, HarvestStats stats, int sites)
    {
        var order = Enumerable.Range(0, sites).ToArray();
        random.Shuffle(order);
        int pairs = Pairs(random, stats, sites);
        var crops = new List<Crop>();
        foreach (int site in order.Take(pairs))
        {
            var (a, b) = PairColours(random, stats);
            crops.Add(new(site, 0, a, Species(random, tables, a, Tiers(random, level, stats))));
            crops.Add(new(site, 1, b, Species(random, tables, b, Tiers(random, level, stats))));
        }
        return Finish(random, tables, stats, crops);
    }

    /// <summary>The memory-line grove's crops: one per site on the first memory_line_num_harvest_plots sites (every site
    /// without it) of a shuffled order, each of 45-47 seeds, of mixed colours under map_harvest_mixed_seed_colours, else of
    /// one colour weighted as a pair's are.</summary>
    public static List<Crop> RollMemoryLine(Random random, HarvestTables tables, HarvestTables.LevelValues level, HarvestStats stats, int sites)
    {
        // Atlas keystones such as Crop Rotation do not apply to memory-line / Overgrown Grove maps.
        var effective = stats.CropRotation ? stats with { CropRotation = false } : stats;
        var order = Enumerable.Range(0, sites).ToArray();
        random.Shuffle(order);
        int plots = effective.MemoryLinePlots > 0 ? Math.Min(effective.MemoryLinePlots, sites) : sites;
        var crops = new List<Crop>();
        foreach (int site in order.Take(plots))
        {
            int count = MemoryLineSeedsMin + random.Next(MemoryLineSeedsMax - MemoryLineSeedsMin + 1);
            var tiers = Tiers(random, level, effective, count);
            if (effective.MixedColours)
            {
                var seeds = MixedSpecies(random, tables, tiers);
                crops.Add(new(site, 0, Majority(tables, seeds, HarvestTables.Red), seeds));
            }
            else
            {
                int colour = PairColours(random, effective).A;
                crops.Add(new(site, 0, colour, Species(random, tables, colour, tiers)));
            }
        }
        return Finish(random, tables, effective, crops);
    }

    // With map_harvest_always_high_tier_seeds (unless Crop Rotation overrides it) one more Tier 4 seed of each colour,
    // in a crop of that colour without one; then the Corpse-Grown seeds.
    static List<Crop> Finish(Random random, HarvestTables tables, HarvestStats stats, List<Crop> crops)
    {
        if (stats.AlwaysHighTier && !stats.CropRotation)
            for (int colour = 1; colour <= 3; colour++)
            {
                if (tables.ByColourTier[colour][4] is not { Length: > 0 } bosses) continue;
                int at = crops.FindIndex(c => c.Colour == colour && !c.Seeds.Any(s => tables.Seeds[s.Row].Tier == 4));
                if (at < 0) continue;
                crops[at] = crops[at] with { Seeds = WithTier4(crops[at].Seeds, tables, bosses[random.Next(bosses.Length)]) };
            }
        if (stats.FaridunChance > 0)
            for (int i = 0; i < crops.Count; i++) crops[i] = crops[i] with { Seeds = WithFaridun(random, tables, crops[i].Seeds, stats.FaridunChance) };
        return crops;
    }

    /// <summary>Each Tier 3 seed becomes its colour's Corpse-Grown seed (the HarvestSeeds FaridunMonster rows, Tier 3) at
    /// map_harvest_%_chance_for_faridun_seed: the wiki's Corpse-Grown monsters replace Tier 3 monsters.</summary>
    static List<SeedCount> WithFaridun(Random random, HarvestTables tables, IReadOnlyList<SeedCount> seeds, int chance)
    {
        var list = new List<SeedCount>();
        var corpse = new Dictionary<int, int>();
        foreach (var s in seeds)
        {
            var seed = tables.Seeds[s.Row];
            if (seed.Tier != 3 || seed.Faridun || tables.Faridun.FirstOrDefault(f => f.Colour == seed.Colour) is not { } faridun)
            {
                list.Add(s);
                continue;
            }
            int turned = Binomial(random, s.Count, chance / 100.0);
            if (s.Count - turned > 0) list.Add(s with { Count = s.Count - turned });
            if (turned > 0) corpse[faridun.Row] = corpse.GetValueOrDefault(faridun.Row) + turned;
        }
        foreach (var (row, count) in corpse) list.Add(new(row, count));
        return list;
    }

    /// <summary>One Tier 1 seed (else the lowest tier's) replaced by a Tier 4 seed, so the crop keeps 23.</summary>
    static List<SeedCount> WithTier4(IReadOnlyList<SeedCount> seeds, HarvestTables tables, int boss)
    {
        var list = seeds.ToList();
        int lowest = list.Select((s, i) => (Tier: tables.Seeds[s.Row].Tier, i)).OrderBy(p => p.Tier).First().i;
        list[lowest] = list[lowest] with { Count = list[lowest].Count - 1 };
        if (list[lowest].Count == 0) list.RemoveAt(lowest);
        list.Insert(0, new(boss, 1));
        return list;
    }

    /// <summary>Crop Rotation on completing a plot: each seed of an idle crop of another colour rolls independently to
    /// rise at most one tier at the level's chance for its current tier (T1→T2, T2→T3, T3→T4 in 1/10000). Newly
    /// upgraded Tier 3 seeds roll <paramref name="faridunChance"/> into that colour's Corpse-Grown seed; existing Tier 3
    /// (including Corpse-Grown) seeds can rise to Tier 4 with no per-crop boss cap. Returns null when nothing changed.</summary>
    public static List<SeedCount>? Upgrade(Random random, HarvestTables tables, HarvestTables.LevelValues level, int colour,
        IReadOnlyList<SeedCount> seeds, int faridunChance = 0)
    {
        var result = new List<SeedCount>();
        var speciesByTier = new Dictionary<(int Colour, int Tier), int>();
        foreach (var s in seeds)
            if (tables.SeedRow(s.Row) is { Faridun: false } existing)
                speciesByTier.TryAdd((existing.Colour is >= HarvestTables.Red and <= HarvestTables.Blue ? existing.Colour : colour, existing.Tier), s.Row);

        int PickSpecies(int seedColour, int tier)
        {
            if (speciesByTier.TryGetValue((seedColour, tier), out int row)) return row;
            var candidates = tables.ByColourTier[seedColour][tier];
            int chosen = candidates[random.Next(candidates.Length)];
            speciesByTier[(seedColour, tier)] = chosen;
            return chosen;
        }

        bool changed = false;
        foreach (var s in seeds)
        {
            if (tables.SeedRow(s.Row) is not { } seed) { result.Add(s); continue; }
            int tier = seed.Tier;
            int seedColour = seed.Colour is >= HarvestTables.Red and <= HarvestTables.Blue ? seed.Colour : colour;
            int chance = tier switch { 1 => level.Upgrade1, 2 => level.Upgrade2, 3 => level.Upgrade3, _ => 0 };
            int[]? next = tier is >= 1 and <= 3 ? tables.ByColourTier[seedColour][tier + 1] : null;
            int up = chance <= 0 || next is not { Length: > 0 } ? 0 : Binomial(random, s.Count, chance / 10000.0);
            if (s.Count - up > 0) result.Add(s with { Count = s.Count - up });
            if (up == 0) continue;
            changed = true;
            int nextTier = tier + 1;
            var faridun = nextTier == 3 && faridunChance > 0 ? tables.Faridun.FirstOrDefault(f => f.Colour == seedColour) : null;
            int turned = faridun is not null ? Binomial(random, up, faridunChance / 100.0) : 0;
            if (up - turned > 0) result.Add(new(PickSpecies(seedColour, nextTier), up - turned));
            if (turned > 0) result.Add(new(faridun!.Row, turned));
        }
        if (!changed) return null;
        return [.. result.GroupBy(s => s.Row)
            .Select(g => new SeedCount(g.Key, g.Sum(s => s.Count)))
            .OrderByDescending(s => tables.SeedRow(s.Row)?.Tier ?? 0)
            .ThenBy(s => tables.SeedRow(s.Row)?.Faridun == true ? 1 : 0)
            .ThenBy(s => s.Row)];
    }
}
