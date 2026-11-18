namespace GameServer.Modules.Harvest;

/// <summary>
/// The Sacred Grove's crop roll, with no state of its own (every roll takes the caller's <see cref="Random"/>): how many
/// pairs of crops, each crop's colour, and its 23 seeds by tier and species (HarvestSeeds, HarvestPerLevelValues).
/// </summary>
public static class GroveCrops
{
    public const int SeedsPerCrop = 23;

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
    /// Binom(rolls, chance); the rest Tier 1. The memory-line rules and Crop Rotation's all-Tier-1 apply after.</summary>
    public static int[] Tiers(Random random, HarvestTables.LevelValues level, HarvestStats stats)
    {
        var tiers = new int[5];
        double t4 = level.Tier4 / 10000.0 * Math.Max(0, 1 + stats.Tier4ChanceIncrease / 100.0) + stats.Tier4FlatChance / 100.0;
        tiers[4] = random.NextDouble() < t4 ? 1 : 0;
        tiers[3] = Binomial(random, level.Tier3Rolls, level.Tier3Chance / 10000.0 * Math.Max(0, 1 + stats.Tier3ChanceIncrease / 100.0));
        tiers[2] = Binomial(random, level.Tier2Rolls, level.Tier2Chance / 10000.0);
        tiers[3] = Math.Min(tiers[3], SeedsPerCrop - tiers[4]);
        tiers[2] = Math.Min(tiers[2], SeedsPerCrop - tiers[4] - tiers[3]);
        tiers[1] = SeedsPerCrop - tiers[4] - tiers[3] - tiers[2];
        if (stats.AtLeastTier2) { tiers[2] += tiers[1]; tiers[1] = 0; }
        if (stats.Tier2UpgradeChance > 0 || stats.Tier3UpgradeChance > 0)
        {
            int up2 = Binomial(random, tiers[1], stats.Tier2UpgradeChance / 100.0);
            int up3 = Binomial(random, tiers[2], stats.Tier3UpgradeChance / 100.0);
            tiers[1] -= up2; tiers[2] += up2 - up3; tiers[3] += up3;
        }
        if (stats.AllTier1) tiers = [0, SeedsPerCrop, 0, 0, 0];
        return tiers;
    }

    /// <summary>A crop's seeds for tier counts: one species per tier, uniform among the colour's species of that tier,
    /// Tier 1 split between two species.</summary>
    public static List<SeedCount> Species(Random random, HarvestTables tables, int colour, int[] tiers)
    {
        var seeds = new List<SeedCount>();
        for (int tier = 4; tier >= 1; tier--)
        {
            if (tiers[tier] == 0 || tables.ByColourTier[colour][tier] is not { Length: > 0 } rows) continue;
            if (tier == 1 && rows.Length > 1 && tiers[1] > 1)
            {
                int a = random.Next(rows.Length), b = (a + 1 + random.Next(rows.Length - 1)) % rows.Length;
                int first = 0;
                for (int i = 0; i < tiers[1]; i++) if (random.Next(2) == 0) first++;
                if (first > 0) seeds.Add(new(rows[a], first));
                if (tiers[1] - first > 0) seeds.Add(new(rows[b], tiers[1] - first));
            }
            else seeds.Add(new(rows[random.Next(rows.Length)], tiers[tier]));
        }
        return seeds;
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
        if (stats.AlwaysHighTier && !stats.AllTier1)
            for (int colour = 1; colour <= 3; colour++)
            {
                if (tables.ByColourTier[colour][4] is not { Length: > 0 } bosses) continue;
                int at = crops.FindIndex(c => c.Colour == colour && !c.Seeds.Any(s => tables.Seeds[s.Row].Tier == 4));
                if (at < 0) continue;
                crops[at] = crops[at] with { Seeds = WithTier4(crops[at].Seeds, tables, bosses[random.Next(bosses.Length)]) };
            }
        return crops;
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

    /// <summary>Crop Rotation on completing a plot: each seed of an unstarted crop of another colour rises a tier at the
    /// level's chance for its tier (T1→T2, T2→T3, T3→T4), into a species of that tier. Null when nothing changed.</summary>
    public static List<SeedCount>? Upgrade(Random random, HarvestTables tables, HarvestTables.LevelValues level, int colour,
        IReadOnlyList<SeedCount> seeds)
    {
        var result = new List<SeedCount>();
        bool changed = false;
        foreach (var s in seeds)
        {
            int tier = tables.Seeds[s.Row].Tier;
            int chance = tier switch { 1 => level.Upgrade1, 2 => level.Upgrade2, 3 => level.Upgrade3, _ => 0 };
            int up = chance <= 0 || tier >= 4 || tables.ByColourTier[colour][tier + 1] is not { Length: > 0 } ? 0
                : Binomial(random, s.Count, chance / 10000.0);
            if (s.Count - up > 0) result.Add(s with { Count = s.Count - up });
            if (up == 0) continue;
            changed = true;
            var next = tables.ByColourTier[colour][tier + 1];
            result.Add(new(next[random.Next(next.Length)], up));
        }
        return changed ? [.. result.GroupBy(s => s.Row).Select(g => new SeedCount(g.Key, g.Sum(s => s.Count)))] : null;
    }
}
