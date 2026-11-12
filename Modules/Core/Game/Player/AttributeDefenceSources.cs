

/// <summary>Ordinary global defence increases from complete groups of final attributes.</summary>
public static class AttributeDefenceSources
{
    // 3.29.3b Stats.dat rows 4993/4997, client keys 4994/4998. Shaper's Touch supplies 1 and 2.
    public const string EnergyShieldPerTenStrength = "energy_shield_+%_per_10_strength";
    public const string EvasionPerTenIntelligence = "evasion_+%_per_10_intelligence";
    static readonly (string Input, string Output)[] StrengthRules =
    [
        ("armour_+%_per_50_str", "physical_damage_reduction_rating_+%"),
        ("block_chance_%_per_50_strength", "additional_block_%"),
    ];

    public static bool IsInput(string name) =>
        name is EnergyShieldPerTenStrength or EvasionPerTenIntelligence
            or "armour_+%_per_50_str" or "block_chance_%_per_50_strength";

    public static void Apply(Dictionary<uint, int> sources, StatCatalog stats, int strength)
    {
        foreach (var (input, output) in StrengthRules)
            if (stats.Key(input) is uint coefficient && sources.TryGetValue(coefficient, out int value)
                && value != 0 && stats.Key(output) is uint target)
                sources[target] = ActorScale.Saturate((long)sources.GetValueOrDefault(target)
                    + (long)value * (Math.Max(0, strength) / 50));
    }
}
