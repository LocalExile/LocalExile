

/// <summary>Native flags that suppress inherent attribute grants while retaining attributes and item group terms.</summary>
public static class InherentAttributeBonuses
{
    // 3.29.3b Stats.dat client keys 7964/7965/15532/18387/18388. Narrow flags do not suppress the other grant.
    public const uint NoLifeFromStrength = Stat.GainNoMaximumLifeFromStrength;
    public const uint NoManaFromIntelligence = Stat.GainNoMaximumManaFromIntelligence;
    public const uint NoStrengthBonuses = Stat.GainNoInherentBonusFromStrength;
    public const uint NoDexterityBonuses = Stat.GainNoInherentBonusFromDexterity;
    public const uint NoIntelligenceBonuses = Stat.GainNoInherentBonusFromIntelligence;

    public static bool IsInput(uint name) => name is NoLifeFromStrength or NoManaFromIntelligence
        or NoStrengthBonuses or NoDexterityBonuses or NoIntelligenceBonuses;

    public static bool IsLifeInput(uint name) => name is NoLifeFromStrength or NoStrengthBonuses;

    public static bool IsManaInput(uint name) => name is NoManaFromIntelligence or NoIntelligenceBonuses;

    public static bool IsEnergyShieldInput(uint name) => name == NoIntelligenceBonuses;
}
