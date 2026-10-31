/// <summary>Signed ordinary flat pool sources. Attribute terms use complete groups of final attributes,
/// before pool increases (pinned PoB ModParser.lua1471-1472 and ModStore.lua501,525).</summary>
static class LifeManaPoolSources
{
    public const string LifePerLevel = "life_per_level";
    public const string ManaPerLevel = "mana_per_level";
    public const string LifePerTenDexterity = "maximum_life_per_10_dexterity";
    public const string LifePerTenIntelligence = "maximum_life_per_10_intelligence";
    public const string LifePerFourDexterity = "X_life_per_4_dexterity";
    public const string LifePerTwoIntelligence = "x_to_maximum_life_per_2_intelligence";

    // CharacterStats.For supplies its clamped level and completed nonnegative attributes. Coefficients stay signed:
    // native life_per_level penalties of -12/-6/-3 subtract from the inherent 12 per level, rather than disappearing.
    public static long AdditionalLife(int level, int finalDexterity, int finalIntelligence, CharacterStats.Bonus bonus) =>
        (long)level * bonus.LifePerLevel
        + Math.Max(0, finalDexterity) / 10 * (long)bonus.LifePerTenDexterity
        + Math.Max(0, finalIntelligence) / 10 * (long)bonus.LifePerTenIntelligence
        + Math.Max(0, finalDexterity) / 4 * (long)bonus.LifePerFourDexterity
        + Math.Max(0, finalIntelligence) / 2 * (long)bonus.LifePerTwoIntelligence;

    public static long AdditionalMana(int level, CharacterStats.Bonus bonus) => (long)level * bonus.ManaPerLevel;

    public static bool IsLifeInput(string name) => name is LifePerLevel or LifePerTenDexterity
        or LifePerTenIntelligence or LifePerFourDexterity or LifePerTwoIntelligence;

    public static bool IsManaInput(string name) => name == ManaPerLevel;

    public static bool IsInput(string name) => IsLifeInput(name) || IsManaInput(name);
}
