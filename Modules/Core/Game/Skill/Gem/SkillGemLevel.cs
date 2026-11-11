

/// <summary>Effective active-gem level; stored experience and intrinsic requirements are independent.</summary>
public static class SkillGemLevel
{
    public static readonly string[] FamilyStats =
        ["herald_skill_gem_level_+", "vaal_skill_gem_level_+", "link_skill_gem_level_+", "raise_zombie_gem_level_+"];

    // "All Skill Gems" means active skill gems (PoB keyword grants_active_skill); supports have their own stat.
    public const string AllSkillGems = "all_skill_gem_level_+";
    public const string NonExceptionalSupports = "non_exceptional_support_gem_level_+";
    public const string AllSkillGemQuality = "all_skill_gem_quality_+";

    public static int Resolve(int intrinsic, int supportedBonus, IEnumerable<int> familyBonuses) =>
        (int)Math.Clamp(intrinsic + (long)supportedBonus + familyBonuses.Sum(value => (long)value), 1, int.MaxValue);

    /// <summary>"+1 to Level of all Strength/Dexterity/Intelligence Skill Gems" (the Honoured Warlord, Hunter and Wise
    /// tattoos): a gem's SkillGems GemColour names the attribute it belongs to - 1 red strength, 2 green dexterity,
    /// 3 blue intelligence; a white (4) gem has none.</summary>
    public static string? AttributeStat(int colour) => colour switch
    {
        1 => "strength_skill_gem_level_+",
        2 => "dexterity_skill_gem_level_+",
        3 => "intelligence_skill_gem_level_+",
        _ => null,
    };
}
