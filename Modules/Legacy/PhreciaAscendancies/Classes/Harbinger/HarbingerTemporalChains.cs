namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

/// <summary>A timed hex or a maintained curse aura. Source stats are captured at application.</summary>
sealed class HarbingerTemporalChains(ushort instance, ushort skill, HarbingerRecipientClock clock, int normalSlow, int rareSlow,
    int otherEffects, bool aura = false)
{
    public ushort Instance { get; set; } = instance;
    public double PresentedDeadline { get; set; } = clock.Deadline;
    public ushort Skill { get; } = skill;
    public HarbingerRecipientClock Clock { get; } = clock;
    public bool Aura { get; } = aura;
    public int Slow(bool rare) => rare ? rareSlow : normalSlow;
    public int OtherEffects { get; } = otherEffects;
    public readonly record struct Source(double Seconds, int NormalSlow, int RareSlow, int OtherEffects);
    public Source Snapshot { get; init; } = new(clock.Remaining, normalSlow, rareSlow, otherEffects);

    internal static Source From(Func<string, int> player, Func<string, int> own, bool self)
    {
        int Get(string name) => name is "temporal_chains_action_speed_+%_final"
            or "temporal_chains_action_speed_+%_vs_rare_or_unique_final"
            or "buff_time_passed_+%_other_than_temporal_chains" or "base_skill_effect_duration"
            or "curse_effect_+%_final_vs_players" ? own(name)
            : (int)Math.Clamp((long)player(name) + own(name), int.MinValue, int.MaxValue);
        double effect = Math.Max(0, 1 + ((long)Get("curse_effect_+%") + Get("temporal_chains_curse_effect_+%")
            + Get("temporal_chains_effeciveness_+%")) / 100.0)
            * Math.Max(0, 1 + Get("support_blasphemy_curse_effect_+%_final") / 100.0);
        if (self) effect *= Math.Max(0, 1 + Get("curse_effect_+%_final_vs_players") / 100.0)
            * Math.Max(0, 1 + player("curse_effect_on_self_+%") / 100.0);
        return new(Duration(Get("base_skill_effect_duration"), player, own, self),
            Scale(Get("temporal_chains_action_speed_+%_final"), effect),
            Scale(Get("temporal_chains_action_speed_+%_vs_rare_or_unique_final"), effect),
            Math.Max(-75, Scale(Get("buff_time_passed_+%_other_than_temporal_chains"), effect)));
    }

    public static double Duration(int baseMilliseconds, Func<string, int> player, Func<string, int> own, bool self)
    {
        // Generic and curse-specific increased skill duration share one source bucket.
        int DurationGet(string name) => name == SkillDurationModifiers.IncreasedDuration
            ? (int)Math.Clamp((long)player(name) + player("curse_duration_+%")
                + player("curse_skill_effect_duration_+%") + player("temporal_chains_duration_+%"), int.MinValue, int.MaxValue) : player(name);
        int OwnGet(string name) => name == SkillDurationModifiers.IncreasedDuration
            ? (int)Math.Clamp((long)own(name) + own("curse_duration_+%") + own("base_curse_duration_+%")
                + own("curse_skill_effect_duration_+%") + own("temporal_chains_duration_+%"), int.MinValue, int.MaxValue) : own(name);
        double seconds = SkillDurationModifiers.Seconds(baseMilliseconds, DurationGet, OwnGet);
        // Recipient self-duration changes the received curse, separately from its source duration and elapsed clock.
        return self ? seconds * Math.Max(0, 1 + player("self_curse_duration_+%") / 100.0) : seconds;
    }

    public static int Scale(int value, double effect) => (int)Math.Truncate(value * Math.Max(0, effect));
    public static double Factor(int slow) => Math.Max(.01, 1 + slow / 100.0);
}
