/// <summary>Recipient elapsed time. Resource rates, periodic triggers, projectile flight and cooldowns are separate.</summary>
static class RecipientEffectTime
{
    public static double Rate(int category, Func<string, int> get, bool temporalChains = false)
    {
        long value = get("buff_time_passed_+%");
        if (!temporalChains) value += get("buff_time_passed_+%_other_than_temporal_chains");
        if (category is 1 or 7 or 13 or 16) value += get("buff_time_passed_+%_only_buff_category");
        if (category is 2 or 5 or 11 or 14) value += get("debuff_time_passed_+%");
        return Math.Max(.25, 1 + value / 100d);
    }
    // Settle at the OLD slope first; an expired effect cannot be resurrected by a slower new slope.
    public static double RetimedDeadline(double deadline, double now, double previousRate, double rate) =>
        deadline <= now ? deadline : now + (deadline - now) * previousRate / Math.Max(.25, rate);
}
