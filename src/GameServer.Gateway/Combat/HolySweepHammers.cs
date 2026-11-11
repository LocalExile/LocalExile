/// <summary>
/// Holy Sweep's holy hammers (GrantedEffectStatSets rows 121 Sweep, 122 SweepAltX). Holy Sweep calls down
/// <c>holy_sweep_number_of_holy_bolts_to_create</c> hammers (3 at gem level 1, 5 at 20) "to land randomly on a number of
/// enemies struck"; Holy Sweep of Hammerfalls calls down "waves of holy hammers for a duration": 3 every
/// <c>holy_sweep_hammerfall_rate_ms</c> (700) for <c>base_skill_effect_duration</c> (3000). Each hammer hits every enemy
/// within <c>active_skill_base_secondary_area_of_effect_radius</c> (12 cells, grown by area of effect) of where it lands,
/// for the skill's hit with <c>holy_sweep_hammerfall_damage_+%_final</c> (-35, Hammerfalls -50) more. Holy Sweep's are
/// "Hits ... cannot be evaded" (its ActiveSkills description; Hammerfalls' says nothing). A hammer lands when its art
/// (MiscAnimated HolySweepHammerFall, falling_hammer_sweep.ao) slams: holy_slam.pet at 0.2497 s.
/// </summary>
public static class HolySweepHammers
{
    /// <summary>Seconds from the hammer's appearance to its slam (falling_hammer_sweep.ao's holy_slam.pet event).</summary>
    public const double FallSeconds = 0.25;

    /// <param name="Count">Hammers per sweep (Holy Sweep) or per wave (Hammerfalls).</param>
    /// <param name="BaseRadius">The hammer's own base radius in cells, before area of effect.</param>
    /// <param name="More">The hammers' damage multiplier.</param>
    /// <param name="RateSeconds">Hammerfalls' time between waves; 0 for Holy Sweep, whose hammers fall once on struck enemies.</param>
    /// <param name="DurationMs">Hammerfalls' base duration; its waves fall while it lasts.</param>
    public sealed record Settings(int Count, int BaseRadius, double More, bool CannotBeEvaded, double RateSeconds, int DurationMs)
    {
        public bool Hammerfall => RateSeconds > 0;
    }

    /// <summary>The skill's hammers, from its stats; null for a skill without them.</summary>
    public static Settings? For(Func<string, int> get)
    {
        int count = get("holy_sweep_number_of_holy_bolts_to_create");
        int radius = get("active_skill_base_secondary_area_of_effect_radius");
        if (count <= 0 || radius <= 0) return null;
        double more = Math.Max(0, 1 + get("holy_sweep_hammerfall_damage_+%_final") / 100.0);
        int rate = Math.Max(0, get("holy_sweep_hammerfall_rate_ms"));
        return new(count, radius, more, CannotBeEvaded: rate == 0, rate / 1000.0, rate > 0 ? Math.Max(0, get("base_skill_effect_duration")) : 0);
    }

    /// <summary>Holy Sweep: up to <paramref name="count"/> different enemies among those struck, at random.</summary>
    public static List<T> Pick<T>(IReadOnlyList<T> struck, int count, Random random)
    {
        var pool = struck.ToList();
        var picked = new List<T>();
        while (picked.Count < count && pool.Count > 0)
        {
            int i = random.Next(pool.Count);
            picked.Add(pool[i]);
            pool.RemoveAt(i);
        }
        return picked;
    }

    /// <summary>Hammerfalls' waves, in seconds after the sweep: one every rate while the duration lasts, the first one rate
    /// after the sweep (3000 / 700: 0.7, 1.4, 2.1 and 2.8 s).</summary>
    public static List<double> WaveTimes(double rateSeconds, double durationSeconds)
    {
        var times = new List<double>();
        if (rateSeconds <= 0) return times;
        for (int wave = 1; wave * rateSeconds <= durationSeconds + 1e-9 && wave <= 1000; wave++) times.Add(wave * rateSeconds);
        return times;
    }

    /// <summary>Seconds from a Hammerfalls wave to its hammers' hits: each hammer is an action of its own that hits
    /// 0.1 s after the wave sets it (+0x348, then its hit 0x141b1a610).</summary>
    public const double HammerfallHitSeconds = 0.1;

    /// <summary>
    /// One Hammerfalls wave, as the client's 0x141b19980 lays it out. Its candidates are the monsters whose distance from
    /// the swing's centre is at most the area's radius plus their size, shuffled, then ordered by the hammers each took
    /// in earlier waves of this hammerfall (fewest first). Each hammer goes to the first candidate not hammered yet this
    /// wave whose roll of 0-99 is under 100 halved once per hammer it took (100, 50, 25 …), or else to a random point
    /// within the radius (an angle and a distance up to the radius; up to 3 tries on walkable ground). The client seeds its
    /// draws from the use's sequence, the wave and the hammer, but its candidates come in its own object order, so the
    /// server draws its own. <paramref name="taken"/> counts each monster's hammers across the waves.
    /// </summary>
    public static List<(uint? Id, (int X, int Y) At)> Wave(IReadOnlyList<(uint Id, (int X, int Y) At, int Size)> monsters,
        Dictionary<uint, int> taken, (int X, int Y) centre, int radius, int count, Func<int, int, bool> walkable, Random random)
    {
        var candidates = monsters.Where(m =>
        {
            long dx = m.At.X - centre.X, dy = m.At.Y - centre.Y, reach = radius + Math.Max(0, m.Size);
            return dx * dx + dy * dy <= reach * reach;
        }).ToList();
        for (int i = 0; i < candidates.Count - 1; i++)
        {
            int j = i + random.Next(candidates.Count - i);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        candidates = [.. candidates.OrderBy(m => taken.GetValueOrDefault(m.Id))];
        var free = Enumerable.Repeat(true, candidates.Count).ToArray();
        var hammers = new List<(uint? Id, (int X, int Y) At)>();
        for (int h = 0; h < count; h++)
        {
            bool placed = false;
            for (int i = 0; i < candidates.Count && !placed; i++)
            {
                if (!free[i]) continue;
                int chance = 100;
                for (int n = taken.GetValueOrDefault(candidates[i].Id); n > 0 && chance > 0; n--) chance /= 2;
                if (random.Next(100) >= chance) continue;
                free[i] = false;
                taken[candidates[i].Id] = taken.GetValueOrDefault(candidates[i].Id) + 1;
                hammers.Add((candidates[i].Id, candidates[i].At));
                placed = true;
            }
            for (int attempt = 0; attempt < 3 && !placed; attempt++)
            {
                double angle = random.NextDouble() * 2 * Math.PI, distance = random.NextDouble() * Math.Max(0, radius);
                var point = (centre.X + (int)Math.Round(distance * Math.Sin(angle)), centre.Y - (int)Math.Round(distance * Math.Cos(angle)));
                if (!walkable(point.Item1, point.Item2)) continue;
                hammers.Add((null, point));
                placed = true;
            }
        }
        return hammers;
    }
}
