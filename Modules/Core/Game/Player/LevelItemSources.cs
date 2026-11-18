public static class LevelItemSources
{
    static readonly (string Input, string Output)[] Rules =
    [
        ("chaos_damage_+%_per_level", "chaos_damage_+%"),
        ("critical_strike_chance_+%_per_level", "critical_strike_chance_+%"),
        ("energy_shield_per_level", "base_maximum_energy_shield"),
        ("life_regeneration_rate_per_minute_per_level", "base_life_regeneration_rate_per_minute"),
    ];

    // The level is part of the qualification/cache key, including explicit character-build previews.
    public static void Apply(Dictionary<uint, int> sources, StatCatalog stats, int level)
    {
        foreach (var (input, output) in Rules)
            if (stats.Key(input) is uint coefficient && sources.TryGetValue(coefficient, out int value)
                && value != 0 && stats.Key(output) is uint target)
                sources[target] = ActorScale.Saturate((long)sources.GetValueOrDefault(target) + (long)value * Math.Max(0, level));
    }
}
