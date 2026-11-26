

/// <summary>A skill's duration from the owner's and the skill's increases and more multipliers. Duration consumers must choose their own
/// inputs; this does not give a minion's own skills or buffs the owner's skill duration.</summary>
public static class SkillDurationModifiers
{
    public const uint IncreasedDuration = Stat.SkillEffectDurationPlusPct;

    /// <summary>What a duration belongs to, for the increases scoped to it (<see cref="Seconds(int, Func{string, int},
    /// Func{string, int}, Kinds)"/>): a CreatesMinion skill's, a warcry's, a Vaal skill's, a Buff skill's, or a skill's
    /// secondary duration (base_secondary_skill_effect_duration) instead of its primary one, or a bow skill's.</summary>
    [Flags]
    public enum Kinds { None = 0, Minion = 1, Warcry = 2, Vaal = 4, Buff = 8, Secondary = 16, Brand = 32, Bow = 128 }

    // The increases scoped to a kind, from the player and the skill alike. base_minion_duration_+% is the player's only
    // (PoB SkillStatMap: Duration INC for SkillType.CreatesMinion).
    static readonly (Kinds Kind, uint Stat)[] TypedIncreases =
    [
        (Kinds.Warcry, Stat.WarcryDurationPlusPct), (Kinds.Vaal, Stat.VaalSkillEffectDurationPlusPct), (Kinds.Buff, Stat.BuffDurationPlusPct),
        (Kinds.Secondary, Stat.SecondarySkillEffectDurationPlusPct), (Kinds.Brand, Stat.SigilDurationPlusPct),
        (Kinds.Bow, Stat.SkillEffectDurationPlusPctWithBowSkills),
    ];

    // Supports' more or less duration, on the supported skill's own stats.
    static readonly uint[] SupportMore =
    [
        Stat.SupportReducedDurationSkillEffectDurationPlusPctFinal, Stat.SupportMoreDurationSkillEffectDurationPlusPctFinal,
        Stat.SupportSwiftAfflictionSkillEffectAndDamagingAilmentDurationPlusPctFinal,
    ];

    // The Duration masteries' more and less duration (PassiveSkillMasteryEffects Duration1 / Duration2), the player's.
    static readonly uint[] PlayerMore =
    [
        Stat.PassiveMasteryMoreSkillEffectDurationPlusPctFinal, Stat.PassiveMasteryLessSkillEffectDurationPlusPctFinal,
    ];

    /// <summary>Call to Arms (<c>keystone_call_to_arms</c>): "100% more Warcry Duration", a final multiplier on a warcry's
    /// duration.</summary>
    public static double CallToArmsMore(Func<uint, int> player) => player(Stat.KeystoneCallToArms) != 0 ? 2 : 1;

    // minion: a CreatesMinion skill, whose duration also takes the player's increased minion duration.
    public static double Seconds(int baseMilliseconds, Func<uint, int> player, Func<uint, int> own, bool minion = false) =>
        Seconds(baseMilliseconds, player, own, minion ? Kinds.Minion : Kinds.None);

    public static double Seconds(int baseMilliseconds, Func<uint, int> player, Func<uint, int> own, Kinds kinds)
    {
        long increased = (long)player(IncreasedDuration) + own(IncreasedDuration)
            + (kinds.HasFlag(Kinds.Minion) ? player(Stat.BaseMinionDurationPlusPct) : 0);
        foreach (var (kind, stat) in TypedIncreases)
            if (kinds.HasFlag(kind)) increased += (long)player(stat) + own(stat);
        double value = Math.Max(0, baseMilliseconds) / 1000d * Math.Max(0, 1 + increased / 100d);
        foreach (uint name in SupportMore) value *= Math.Max(0, 1 + own(name) / 100d);
        foreach (uint name in PlayerMore) value *= Math.Max(0, 1 + player(name) / 100d);
        // Swiftbrand: less duration of a brand's primary or secondary duration, each its own stat.
        value *= Math.Max(0, 1 + own(kinds.HasFlag(Kinds.Secondary)
            ? Stat.SupportRapidActivationBrandSkillOnlySecondaryDurationPlusPctFinal
            : Stat.SupportRapidActivationBrandSkillOnlyPrimaryDurationPlusPctFinal) / 100d);
        return Math.Min(86400, value);
    }
}

public static partial class Game
{
    // The duration kinds a gem skill's types give it (SkillDurationModifiers.Kinds): Warcry, Vaal and Buff.
    public static SkillDurationModifiers.Kinds DurationKinds(Player player, int effect)
    {
        var skills = SkillsFor(player);
        return (skills.IsWarcry(effect) ? SkillDurationModifiers.Kinds.Warcry : 0)
            | (skills.IsVaal(effect) ? SkillDurationModifiers.Kinds.Vaal : 0)
            | (skills.HasType(effect, "Buff") ? SkillDurationModifiers.Kinds.Buff : 0)
            | (skills.IsBrand(effect) ? SkillDurationModifiers.Kinds.Brand : 0);
    }
}
