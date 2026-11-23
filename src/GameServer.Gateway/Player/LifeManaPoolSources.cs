/// <summary>Signed ordinary flat pool sources. Attribute terms use complete groups of final attributes,
/// before pool increases (pinned PoB ModParser.lua1471-1472 and ModStore.lua501,525).</summary>
public static class LifeManaPoolSources
{
    public const uint LifePerLevel = Stat.LifePerLevel;
    public const uint ManaPerLevel = Stat.ManaPerLevel;
    public const uint LifePerTenDexterity = Stat.MaximumLifePer10Dexterity;
    public const uint LifePerTenIntelligence = Stat.MaximumLifePer10Intelligence;
    public const uint LifePerFourDexterity = Stat.XLifePer4Dexterity;
    public const uint LifePerTwoIntelligence = Stat.XToMaximumLifePer2Intelligence;

    // CharacterStats.For supplies its clamped level and completed nonnegative attributes. Coefficients stay signed:
    // native life_per_level penalties of -12/-6/-3 subtract from the inherent 12 per level, rather than disappearing.
    public static long AdditionalLife(int level, int finalDexterity, int finalIntelligence, CharacterStats.Bonus bonus) =>
        (long)level * bonus.LifePerLevel
        + Math.Max(0, finalDexterity) / 10 * (long)bonus.LifePerTenDexterity
        + Math.Max(0, finalIntelligence) / 10 * (long)bonus.LifePerTenIntelligence
        + Math.Max(0, finalDexterity) / 4 * (long)bonus.LifePerFourDexterity
        + Math.Max(0, finalIntelligence) / 2 * (long)bonus.LifePerTwoIntelligence;

    public static long AdditionalMana(int level, CharacterStats.Bonus bonus) => (long)level * bonus.ManaPerLevel;

    public static bool IsLifeInput(uint name) => name is LifePerLevel or LifePerTenDexterity
        or LifePerTenIntelligence or LifePerFourDexterity or LifePerTwoIntelligence;

    public static bool IsManaInput(uint name) => name == ManaPerLevel;

    public static bool IsInput(uint name) => IsLifeInput(name) || IsManaInput(name);
}
