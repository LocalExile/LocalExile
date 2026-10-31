

/// <summary>Effective active-gem level; stored experience and intrinsic requirements are independent.</summary>
static class SkillGemLevel
{
    public static readonly string[] FamilyStats =
        ["herald_skill_gem_level_+", "vaal_skill_gem_level_+", "link_skill_gem_level_+", "raise_zombie_gem_level_+"];

    // "All Skill Gems" means active skill gems (PoB keyword grants_active_skill); supports have their own stat.
    public const string AllSkillGems = "all_skill_gem_level_+";
    public const string NonExceptionalSupports = "non_exceptional_support_gem_level_+";
    public const string AllSkillGemQuality = "all_skill_gem_quality_+";

    public static int Resolve(int intrinsic, int supportedBonus, IEnumerable<int> familyBonuses) =>
        (int)Math.Clamp(intrinsic + (long)supportedBonus + familyBonuses.Sum(value => (long)value), 1, int.MaxValue);
}
