/// <summary>
/// Global modifiers to a socketed gem's effective properties. They never change its stored experience or quality.
/// "Skill gems" means active skill gems; supports have their separate non-exceptional modifier.
/// </summary>
readonly record struct PassiveGemModifiers(int SkillLevels = 0, int SkillQuality = 0, int NonExceptionalSupportLevels = 0)
{
    public static PassiveGemModifiers From(Func<string, int> get) => new(
        get("all_skill_gem_level_+"), get("all_skill_gem_quality_+"), get("non_exceptional_support_gem_level_+"));

    /// <summary>The level and quality to use for skill stats, damage, reservation and recovery. Requirements and
    /// experience thresholds still use the gem's intrinsic level. Exceptional supports (Empower, Enhance, Enlighten)
    /// do not receive the non-exceptional support bonus.</summary>
    public (int Level, int Quality) Apply(int intrinsicLevel, int intrinsicQuality, bool support = false, bool exceptional = false)
    {
        int levels = support ? (exceptional ? 0 : NonExceptionalSupportLevels) : SkillLevels;
        int quality = support ? 0 : SkillQuality;
        return ((int)Math.Clamp((long)intrinsicLevel + levels, 1, int.MaxValue),
            (int)Math.Clamp((long)intrinsicQuality + quality, 0, int.MaxValue));
    }
}
