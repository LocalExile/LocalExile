/// <summary>Recipient elapsed time. Resource rates, periodic triggers, projectile flight and cooldowns are separate.</summary>
public static class RecipientEffectTime
{
    public static double Rate(int category, Func<uint, int> get, bool temporalChains = false)
    {
        long value = get(Stat.BuffTimePassedPlusPct);
        if (!temporalChains) value += get(Stat.BuffTimePassedPlusPctOtherThanTemporalChains);
        if (category is 1 or 7 or 13 or 16) value += get(Stat.BuffTimePassedPlusPctOnlyBuffCategory);
        if (category is 2 or 5 or 11 or 14) value += get(Stat.DebuffTimePassedPlusPct);
        return Math.Max(.25, 1 + value / 100d);
    }
    // Settle at the OLD slope first; an expired effect cannot be resurrected by a slower new slope.
    public static double RetimedDeadline(double deadline, double now, double previousRate, double rate) =>
        deadline <= now ? deadline : now + (deadline - now) * previousRate / Math.Max(.25, rate);
}
