namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

/// <summary>A timed hex or a maintained curse aura. Source stats are captured at application.</summary>
sealed class HarbingerTemporalChains(ushort instance, ushort skill, HarbingerRecipientClock clock, int normalSlow, int rareSlow,
    int otherEffects, bool aura = false)
{
    public ushort Instance { get; set; } = instance;
    public ushort Skill { get; } = skill;
    public HarbingerRecipientClock Clock { get; } = clock;
    public bool Aura { get; } = aura;
    public int Slow(bool rare) => rare ? rareSlow : normalSlow;
    public int OtherEffects { get; } = otherEffects;
    public readonly record struct Source(double Seconds, int NormalSlow, int RareSlow, int OtherEffects);
    public Source Snapshot { get; init; } = new(clock.Remaining, normalSlow, rareSlow, otherEffects);

    internal static Source From(Func<uint, int> player, Func<uint, int> own, bool self)
    {
        int Get(uint name) => name is Stat.TemporalChainsActionSpeedPlusPctFinal
            or Stat.TemporalChainsActionSpeedPlusPctVsRareOrUniqueFinal
            or Stat.BuffTimePassedPlusPctOtherThanTemporalChains or Stat.BaseSkillEffectDuration
            or Stat.CurseEffectPlusPctFinalVsPlayers ? own(name)
            : (int)Math.Clamp((long)player(name) + own(name), int.MinValue, int.MaxValue);
        double effect = Math.Max(0, 1 + ((long)Get(Stat.CurseEffectPlusPct) + Get(Stat.TemporalChainsCurseEffectPlusPct)
            + Get(Stat.TemporalChainsEffecivenessPlusPct)) / 100.0)
            * Math.Max(0, 1 + Get(Stat.SupportBlasphemyCurseEffectPlusPctFinal) / 100.0);
        if (self) effect *= Math.Max(0, 1 + Get(Stat.CurseEffectPlusPctFinalVsPlayers) / 100.0)
            * Math.Max(0, 1 + player(Stat.CurseEffectOnSelfPlusPct) / 100.0);
        return new(Duration(Get(Stat.BaseSkillEffectDuration), player, own, self),
            Scale(Get(Stat.TemporalChainsActionSpeedPlusPctFinal), effect),
            Scale(Get(Stat.TemporalChainsActionSpeedPlusPctVsRareOrUniqueFinal), effect),
            Math.Max(-75, Scale(Get(Stat.BuffTimePassedPlusPctOtherThanTemporalChains), effect)));
    }

    public static double Duration(int baseMilliseconds, Func<uint, int> player, Func<uint, int> own, bool self)
    {
        // Generic and curse-specific increased skill duration share one source bucket.
        int DurationGet(uint name) => name == SkillDurationModifiers.IncreasedDuration
            ? (int)Math.Clamp((long)player(name) + player(Stat.CurseDurationPlusPct)
                + player(Stat.CurseSkillEffectDurationPlusPct) + player(Stat.TemporalChainsDurationPlusPct), int.MinValue, int.MaxValue) : player(name);
        int OwnGet(uint name) => name == SkillDurationModifiers.IncreasedDuration
            ? (int)Math.Clamp((long)own(name) + own(Stat.CurseDurationPlusPct) + own(Stat.BaseCurseDurationPlusPct)
                + own(Stat.CurseSkillEffectDurationPlusPct) + own(Stat.TemporalChainsDurationPlusPct), int.MinValue, int.MaxValue) : own(name);
        double seconds = SkillDurationModifiers.Seconds(baseMilliseconds, DurationGet, OwnGet);
        // Recipient self-duration changes the received curse, separately from its source duration and elapsed clock.
        return self ? seconds * Math.Max(0, 1 + player(Stat.SelfCurseDurationPlusPct) / 100.0) : seconds;
    }

    public static int Scale(int value, double effect) => (int)Math.Truncate(value * Math.Max(0, effect));
    public static double Factor(int slow) => Math.Max(.01, 1 + slow / 100.0);
}
