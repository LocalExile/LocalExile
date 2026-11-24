

/// <summary>Ordinary global defence increases from complete groups of final attributes.</summary>
public static class AttributeDefenceSources
{
    // 3.29.3b Stats.dat rows 4993/4997, client keys 4994/4998. Shaper's Touch supplies 1 and 2.
    public const uint EnergyShieldPerTenStrength = Stat.EnergyShieldPlusPctPer10Strength;
    public const uint EvasionPerTenIntelligence = Stat.EvasionPlusPctPer10Intelligence;
    // The Evasion and Energy Shield Mastery's spelling of the same coefficient.
    public const uint MasteryEvasionPerTenIntelligence = Stat.EvasionRatingPlusPctPer10Intelligence;
    static readonly (uint Input, uint Output, int Per)[] StrengthRules =
    [
        (Stat.ArmourPlusPctPer50Str, Stat.PhysicalDamageReductionRatingPlusPct, 50),
        (Stat.BlockChancePctPer50Strength, Stat.AdditionalBlockPct, 50),
        // The Armour Mastery's "+1 Armour per 2 Strength", a flat rating.
        (Stat.XToArmourPer2Strength, Stat.BasePhysicalDamageReductionRating, 2),
    ];

    public static bool IsInput(uint name) =>
        name is EnergyShieldPerTenStrength or EvasionPerTenIntelligence or MasteryEvasionPerTenIntelligence
            or Stat.ArmourPlusPctPer50Str or Stat.BlockChancePctPer50Strength or Stat.XToArmourPer2Strength;

    public static void Apply(Dictionary<uint, int> sources, StatCatalog stats, int strength)
    {
        foreach (var (input, output, per) in StrengthRules)
            if (input is var coefficient && sources.TryGetValue(coefficient, out int value)
                && value != 0 && output is var target)
                sources[target] = ActorScale.Saturate((long)sources.GetValueOrDefault(target)
                    + (long)value * (Math.Max(0, strength) / per));
    }
}
