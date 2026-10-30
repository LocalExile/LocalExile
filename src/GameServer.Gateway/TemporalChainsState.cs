/// <summary>A timed hex or a maintained curse aura. Source stats are captured at application.</summary>
sealed class TemporalChainsState(ushort instance, ushort skill, EffectClock clock, int normalSlow, int rareSlow,
    int otherEffects, bool aura = false)
{
    public ushort Instance { get; set; } = instance;
    public double PresentedDeadline { get; set; } = clock.Deadline;
    public ushort Skill { get; } = skill;
    public EffectClock Clock { get; } = clock;
    public bool Aura { get; } = aura;
    public int Slow(bool rare) => rare ? rareSlow : normalSlow;
    public int OtherEffects { get; } = otherEffects;
    public readonly record struct Source(double Seconds, int NormalSlow, int RareSlow, int OtherEffects);
    public Source Snapshot { get; init; } = new(clock.Remaining, normalSlow, rareSlow, otherEffects);

    public static double Duration(int baseMilliseconds, Func<string, int> player, Func<string, int> own, bool self)
    {
        // Generic and curse-specific increased skill duration share one source bucket.
        int DurationGet(string name) => name == SkillDurationModifiers.IncreasedDuration
            ? (int)Math.Clamp((long)player(name) + player("curse_duration_+%")
                + player("curse_skill_effect_duration_+%"), int.MinValue, int.MaxValue) : player(name);
        int OwnGet(string name) => name == SkillDurationModifiers.IncreasedDuration
            ? (int)Math.Clamp((long)own(name) + own("curse_duration_+%") + own("base_curse_duration_+%")
                + own("curse_skill_effect_duration_+%"), int.MinValue, int.MaxValue) : own(name);
        double seconds = SkillDurationModifiers.Seconds(baseMilliseconds, DurationGet, OwnGet);
        // Recipient self-duration changes the received curse, separately from its source duration and elapsed clock.
        return self ? seconds * Math.Max(0, 1 + player("self_curse_duration_+%") / 100.0) : seconds;
    }

    public static int Scale(int value, double effect) => (int)Math.Truncate(value * Math.Max(0, effect));
    public static double Factor(int slow) => Math.Max(.01, 1 + slow / 100.0);
}
