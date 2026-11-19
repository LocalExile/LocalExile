

/// <summary>Owner skill-duration increases derived from final attributes. Duration consumers must choose their own
/// inputs; this does not give a minion's own skills or buffs the owner's skill duration.</summary>
public static class SkillDurationModifiers
{
    public const uint PerTenStrength = Stat.SkillEffectDurationPlusPctPer10Strength;
    public const uint IncreasedDuration = Stat.SkillEffectDurationPlusPct;

    public static int Total(int rawIncreasedDuration, int finalStrength, int increasedPerTenStrength) =>
        (int)Math.Clamp(rawIncreasedDuration + (long)(Math.Max(0, finalStrength) / 10) * increasedPerTenStrength,
            int.MinValue, int.MaxValue);

    // minion: a CreatesMinion skill, whose duration also takes the player's increased minion duration (PoB SkillStatMap
    // base_minion_duration_+% is Duration INC for SkillType.CreatesMinion).
    public static double Seconds(int baseMilliseconds, Func<uint, int> player, Func<uint, int> own, bool minion = false)
    {
        double value = Math.Max(0, baseMilliseconds) / 1000d
            * Math.Max(0, 1 + ((long)player(IncreasedDuration) + own(IncreasedDuration)
                + (minion ? player(Stat.BaseMinionDurationPlusPct) : 0)) / 100d);
        foreach (uint name in new[] { Stat.SupportReducedDurationSkillEffectDurationPlusPctFinal,
            Stat.SupportMoreDurationSkillEffectDurationPlusPctFinal,
            Stat.SupportSwiftAfflictionSkillEffectAndDamagingAilmentDurationPlusPctFinal })
            value *= Math.Max(0, 1 + own(name) / 100d);
        return Math.Min(86400, value);
    }
}
