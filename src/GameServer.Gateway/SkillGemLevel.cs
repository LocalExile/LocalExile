/// <summary>Effective active-gem level; stored experience and intrinsic requirements are independent.</summary>
static class SkillGemLevel
{
    public static readonly string[] FamilyStats =
        ["herald_skill_gem_level_+", "vaal_skill_gem_level_+", "link_skill_gem_level_+", "raise_zombie_gem_level_+"];

    public static int Resolve(int intrinsic, int supportedBonus, IEnumerable<int> familyBonuses) =>
        (int)Math.Clamp(intrinsic + (long)supportedBonus + familyBonuses.Sum(value => (long)value), 1, int.MaxValue);
}
