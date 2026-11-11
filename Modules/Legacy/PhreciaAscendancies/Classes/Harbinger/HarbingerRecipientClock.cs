
namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

/// <summary>Remaining effect time, independent of world time, cooldowns and damage/recovery rates.</summary>
sealed class HarbingerRecipientClock(double duration, double now, double rate = 1)
{
    public double Remaining { get; private set; } = Math.Max(0, duration);
    public double Rate { get; private set; } = Math.Max(0, rate);
    double sampled = now;
    public double Deadline { get; private set; } = duration <= 0 ? now : rate > 0 ? now + duration / rate : double.PositiveInfinity;

    public void Advance(double now)
    {
        if (now < sampled) return;
        Remaining = Math.Max(0, Remaining - (now - sampled) * Rate);
        sampled = now;
    }

    public void ChangeRate(double now, double rate)
    {
        Advance(now);
        double next = Math.Max(0, rate);
        if (next == Rate) return;
        Rate = next;
        Deadline = Remaining <= 0 ? now : Rate > 0 ? now + Remaining / Rate : double.PositiveInfinity;
    }

    // Pinned client 0x141f7fa30: the category masks, additive elapsed inputs and minimum rate are native rules.
    // Temporal Chains excludes its own other-effects term. Cooldowns/resources never call this method.
    public static double RecipientRate(int category, Func<string, int> get, bool temporalChains = false)
    {
        return RecipientEffectTime.Rate(category, get, temporalChains);
    }
}
