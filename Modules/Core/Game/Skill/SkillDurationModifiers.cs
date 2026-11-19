

/// <summary>Owner skill-duration increases derived from final attributes. Duration consumers must choose their own
/// inputs; this does not give a minion's own skills or buffs the owner's skill duration.</summary>
public static class SkillDurationModifiers
{
    public const string PerTenStrength = "skill_effect_duration_+%_per_10_strength";
    public const string IncreasedDuration = "skill_effect_duration_+%";

    /// <summary>What a duration belongs to, for the increases scoped to it (<see cref="Seconds(int, Func{string, int},
    /// Func{string, int}, Kinds)"/>): a CreatesMinion skill's, a warcry's, a Vaal skill's, a Buff skill's, or a skill's
    /// secondary duration (base_secondary_skill_effect_duration) instead of its primary one.</summary>
    [Flags]
    public enum Kinds { None = 0, Minion = 1, Warcry = 2, Vaal = 4, Buff = 8, Secondary = 16 }

    // The increases scoped to a kind, from the player and the skill alike. base_minion_duration_+% is the player's only
    // (PoB SkillStatMap: Duration INC for SkillType.CreatesMinion).
    static readonly (Kinds Kind, string Stat)[] TypedIncreases =
    [
        (Kinds.Warcry, "warcry_duration_+%"), (Kinds.Vaal, "vaal_skill_effect_duration_+%"), (Kinds.Buff, "buff_duration_+%"),
        (Kinds.Secondary, "secondary_skill_effect_duration_+%"),
    ];

    // Supports' more or less duration, on the supported skill's own stats.
    static readonly string[] SupportMore =
    [
        "support_reduced_duration_skill_effect_duration_+%_final", "support_more_duration_skill_effect_duration_+%_final",
        "support_swift_affliction_skill_effect_and_damaging_ailment_duration_+%_final",
    ];

    // The Duration masteries' more and less duration (PassiveSkillMasteryEffects Duration1 / Duration2), the player's.
    static readonly string[] PlayerMore =
    [
        "passive_mastery_more_skill_effect_duration_+%_final", "passive_mastery_less_skill_effect_duration_+%_final",
    ];

    public static int Total(int rawIncreasedDuration, int finalStrength, int increasedPerTenStrength) =>
        (int)Math.Clamp(rawIncreasedDuration + (long)(Math.Max(0, finalStrength) / 10) * increasedPerTenStrength,
            int.MinValue, int.MaxValue);

    // minion: a CreatesMinion skill, whose duration also takes the player's increased minion duration.
    public static double Seconds(int baseMilliseconds, Func<string, int> player, Func<string, int> own, bool minion = false) =>
        Seconds(baseMilliseconds, player, own, minion ? Kinds.Minion : Kinds.None);

    public static double Seconds(int baseMilliseconds, Func<string, int> player, Func<string, int> own, Kinds kinds)
    {
        long increased = (long)player(IncreasedDuration) + own(IncreasedDuration)
            + (kinds.HasFlag(Kinds.Minion) ? player("base_minion_duration_+%") : 0);
        foreach (var (kind, stat) in TypedIncreases)
            if (kinds.HasFlag(kind)) increased += (long)player(stat) + own(stat);
        double value = Math.Max(0, baseMilliseconds) / 1000d * Math.Max(0, 1 + increased / 100d);
        foreach (string name in SupportMore) value *= Math.Max(0, 1 + own(name) / 100d);
        foreach (string name in PlayerMore) value *= Math.Max(0, 1 + player(name) / 100d);
        // Swiftbrand: less duration of a brand's primary or secondary duration, each its own stat.
        value *= Math.Max(0, 1 + own(kinds.HasFlag(Kinds.Secondary)
            ? "support_rapid_activation_brand_skill_only_secondary_duration_+%_final"
            : "support_rapid_activation_brand_skill_only_primary_duration_+%_final") / 100d);
        return Math.Min(86400, value);
    }
}

public static partial class Game
{
    // The duration kinds a gem skill's types give it (SkillDurationModifiers.Kinds): Warcry, Vaal and Buff.
    public static SkillDurationModifiers.Kinds DurationKinds(ClientState conn, int effect)
    {
        var skills = SkillsFor(conn);
        return (skills.IsWarcry(effect) ? SkillDurationModifiers.Kinds.Warcry : 0)
            | (skills.IsVaal(effect) ? SkillDurationModifiers.Kinds.Vaal : 0)
            | (skills.HasType(effect, "Buff") ? SkillDurationModifiers.Kinds.Buff : 0);
    }
}
