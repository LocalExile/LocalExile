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
static class HolySweepHammers
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

    /// <summary>Hammerfalls: a random point, uniform over the sweep's area around <paramref name="centre"/>.</summary>
    public static (int X, int Y) PointWithin((int X, int Y) centre, int radius, Random random)
    {
        double r = Math.Max(0, radius) * Math.Sqrt(random.NextDouble()), angle = random.NextDouble() * 2 * Math.PI;
        return (centre.X + (int)Math.Round(r * Math.Cos(angle)), centre.Y + (int)Math.Round(r * Math.Sin(angle)));
    }
}
