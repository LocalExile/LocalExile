/// <summary>Offence coefficients use completed full maximum pools, independently of spending and reservation.</summary>
public static class MaximumPoolItemSources
{
    public const string SpellCriticalPerLife = "spell_critical_strike_chance_+%_per_100_max_life";
    static readonly (string Input, string Output, bool LowestPool)[] Rules =
    [
        ("spell_damage_+%_per_100_max_life", "spell_damage_+%", false),
        (SpellCriticalPerLife, "spell_critical_strike_chance_+%", false),
        ("minimum_added_fire_damage_per_100_lowest_of_max_life_mana", "global_minimum_added_fire_damage", true),
        ("maximum_added_fire_damage_per_100_lowest_of_max_life_mana", "global_maximum_added_fire_damage", true),
    ];

    public static long Amount(int coefficient, uint maximum) => (long)coefficient * (maximum / 100);

    public static void Apply(Dictionary<uint, int> sources, StatCatalog stats, uint maximumLife, uint maximumMana,
        IReadOnlySet<uint>? replacements = null)
    {
        foreach (var (input, output, lowest) in Rules)
            if (stats.Key(input) is uint key && sources.GetValueOrDefault(key) is int coefficient and not 0
                && stats.Key(output) is uint target && replacements?.Contains(target) != true)
                sources[target] = ActorScale.Saturate((long)sources.GetValueOrDefault(target)
                    + Amount(coefficient, lowest ? Math.Min(maximumLife, maximumMana) : maximumLife));
    }
}
