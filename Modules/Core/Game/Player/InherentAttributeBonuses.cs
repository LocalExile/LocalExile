

/// <summary>Native flags that suppress inherent attribute grants while retaining attributes and item group terms.</summary>
static class InherentAttributeBonuses
{
    // 3.29.3b Stats.dat client keys 7964/7965/15532/18387/18388. Narrow flags do not suppress the other grant.
    public const string NoLifeFromStrength = "gain_no_maximum_life_from_strength";
    public const string NoManaFromIntelligence = "gain_no_maximum_mana_from_intelligence";
    public const string NoStrengthBonuses = "gain_no_inherent_bonus_from_strength";
    public const string NoDexterityBonuses = "gain_no_inherent_bonus_from_dexterity";
    public const string NoIntelligenceBonuses = "gain_no_inherent_bonus_from_intelligence";

    public static bool IsInput(string name) => name is NoLifeFromStrength or NoManaFromIntelligence
        or NoStrengthBonuses or NoDexterityBonuses or NoIntelligenceBonuses;

    public static bool IsLifeInput(string name) => name is NoLifeFromStrength or NoStrengthBonuses;

    public static bool IsManaInput(string name) => name is NoManaFromIntelligence or NoIntelligenceBonuses;

    public static bool IsEnergyShieldInput(string name) => name == NoIntelligenceBonuses;
}
