

/// <summary>Ordinary global defence increases from complete groups of final attributes.</summary>
static class AttributeDefenceSources
{
    // 3.29.3b Stats.dat rows 4993/4997, client keys 4994/4998. Shaper's Touch supplies 1 and 2.
    public const string EnergyShieldPerTenStrength = "energy_shield_+%_per_10_strength";
    public const string EvasionPerTenIntelligence = "evasion_+%_per_10_intelligence";

    public static bool IsInput(string name) =>
        name is EnergyShieldPerTenStrength or EvasionPerTenIntelligence;
}
