/// <summary>Qualified global coefficients use complete groups of final owner attributes.</summary>
public static class AttributeOffenceSources
{
    static readonly (string Input, string Output, int Attribute, int Group)[] Rules =
    [
        ("area_of_effect_+%_per_20_int", "base_skill_area_of_effect_+%", 2, 20),
        ("attack_speed_+%_per_10_dex", "attack_speed_+%", 1, 10),
        ("attack_speed_+%_per_25_dex", "attack_speed_+%", 1, 25),
        ("critical_strike_chance_+%_per_25_intelligence", "critical_strike_chance_+%", 2, 25),
        ("damage_+%_per_15_dex", "damage_+%", 1, 15),
        ("elemental_damage_+%_per_10_dexterity", "elemental_damage_+%", 1, 10),
        ("fire_damage_+%_per_20_strength", "fire_damage_+%", 0, 20),
        ("spell_damage_+%_per_10_int", "spell_damage_+%", 2, 10),
        ("X_accuracy_per_2_intelligence", "accuracy_rating", 2, 2),
        ("bleed_duration_per_12_intelligence_+%", "bleed_duration_+%", 2, 12),
        ("physical_damage_over_time_per_10_dexterity_+%", "physical_damage_over_time_+%", 1, 10),
    ];

    // Apply once to a fresh qualified source sum, before pools, skill snapshots and panels consume it.
    public static void Apply(Dictionary<uint, int> sources, StatCatalog stats,
        int strength, int dexterity, int intelligence)
    {
        int[] attributes = [Math.Max(0, strength), Math.Max(0, dexterity), Math.Max(0, intelligence)];
        Dictionary<uint, long> added = [];
        foreach (var (input, output, attribute, group) in Rules)
            if (stats.Key(input) is uint coefficient && sources.TryGetValue(coefficient, out int value)
                && value != 0 && stats.Key(output) is uint target)
                added[target] = added.GetValueOrDefault(target) + (long)value * (attributes[attribute] / group);
        foreach (var (key, value) in added)
            sources[key] = (int)Math.Clamp((long)sources.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
    }
}
