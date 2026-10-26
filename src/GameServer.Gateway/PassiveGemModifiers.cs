/// <summary>
/// Global modifiers to a socketed gem's effective properties. They never change its stored experience or quality.
/// "Skill gems" means active skill gems; supports have their separate non-exceptional modifier.
/// </summary>
readonly record struct PassiveGemModifiers(int SkillLevels = 0, int SkillQuality = 0, int NonExceptionalSupportLevels = 0,
    IReadOnlyList<(string Stat, int Levels)>? FamilyLevels = null)
{
    /// <summary>The passive tree's family gem-level stats (the Phrecia classes'): each raises only the skill gems of
    /// its family (SkillCatalog.FamilyStatRaises) — the Herald, Vaal and Link gems of those tags, and Raise Zombie's
    /// own stat.</summary>
    public static readonly string[] FamilyLevelStats =
        ["herald_skill_gem_level_+", "vaal_skill_gem_level_+", "link_skill_gem_level_+", "raise_zombie_gem_level_+"];

    public static PassiveGemModifiers From(Func<string, int> get) => new(
        get("all_skill_gem_level_+"), get("all_skill_gem_quality_+"), get("non_exceptional_support_gem_level_+"),
        [.. FamilyLevelStats.Select(stat => (Stat: stat, Levels: get(stat))).Where(f => f.Levels != 0)]);

    /// <summary>The level and quality to use for skill stats, damage, reservation and recovery. Requirements and
    /// experience thresholds still use the gem's intrinsic level. An active gem of a family gets the global bonus and
    /// its family's, composed here once before the one clamp (<paramref name="family"/> says the stat raises the gem).
    /// Supports receive none of the active-only bonuses; the exceptional supports (Empower, Enhance, Enlighten) do not
    /// receive the non-exceptional support bonus either.</summary>
    public (int Level, int Quality) Apply(int intrinsicLevel, int intrinsicQuality, bool support = false,
        bool exceptional = false, Func<string, bool>? family = null)
    {
        long familyLevels = support || family is null || FamilyLevels is not { Count: > 0 } ? 0
            : FamilyLevels.Where(f => family(f.Stat)).Sum(f => (long)f.Levels);
        long levels = (support ? (exceptional ? 0 : NonExceptionalSupportLevels) : SkillLevels) + (support ? 0 : familyLevels);
        int quality = support ? 0 : SkillQuality;
        return ((int)Math.Clamp(intrinsicLevel + levels, 1, int.MaxValue),
            (int)Math.Clamp((long)intrinsicQuality + quality, 0, int.MaxValue));
    }
}
