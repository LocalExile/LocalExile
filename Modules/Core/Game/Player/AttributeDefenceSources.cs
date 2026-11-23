

/// <summary>Ordinary global defence increases from complete groups of final attributes.</summary>
public static class AttributeDefenceSources
{
    // 3.29.3b Stats.dat rows 4993/4997, client keys 4994/4998. Shaper's Touch supplies 1 and 2.
    public const uint EnergyShieldPerTenStrength = Stat.EnergyShieldPlusPctPer10Strength;
    public const uint EvasionPerTenIntelligence = Stat.EvasionPlusPctPer10Intelligence;
    static readonly (uint Input, uint Output)[] StrengthRules =
    [
        (Stat.ArmourPlusPctPer50Str, Stat.PhysicalDamageReductionRatingPlusPct),
        (Stat.BlockChancePctPer50Strength, Stat.AdditionalBlockPct),
    ];

    public static bool IsInput(uint name) =>
        name is EnergyShieldPerTenStrength or EvasionPerTenIntelligence
            or Stat.ArmourPlusPctPer50Str or Stat.BlockChancePctPer50Strength;

    public static void Apply(Dictionary<uint, int> sources, StatCatalog stats, int strength)
    {
        foreach (var (input, output) in StrengthRules)
            if (input is var coefficient && sources.TryGetValue(coefficient, out int value)
                && value != 0 && output is var target)
                sources[target] = ActorScale.Saturate((long)sources.GetValueOrDefault(target)
                    + (long)value * (Math.Max(0, strength) / 50));
    }
}
