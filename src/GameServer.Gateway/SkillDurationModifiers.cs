/// <summary>Owner skill-duration increases derived from final attributes. This helper does not assign the owner's
/// duration to a minion's own skills or to buffs on the owner; each duration consumer must choose its own inputs.</summary>
static class SkillDurationModifiers
{
    public const string PerTenStrength = "skill_effect_duration_+%_per_10_strength";
    public const string IncreasedDuration = "skill_effect_duration_+%";

    static long StrengthContribution(int finalStrength, int increasedPerTenStrength) =>
        (long)(Math.Max(0, finalStrength) / 10) * increasedPerTenStrength;

    public static int StrengthBonus(int finalStrength, int increasedPerTenStrength) =>
        (int)Math.Clamp(StrengthContribution(finalStrength, increasedPerTenStrength), int.MinValue, int.MaxValue);

    public static int Total(int rawIncreasedDuration, int finalStrength, int increasedPerTenStrength) =>
        (int)Math.Clamp(rawIncreasedDuration + StrengthContribution(finalStrength, increasedPerTenStrength),
            int.MinValue, int.MaxValue);
}
