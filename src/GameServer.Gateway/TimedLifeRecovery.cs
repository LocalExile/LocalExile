/// <summary>A paid skill's finite recovery, integrated once on the owner's pausable clock.</summary>
sealed class TimedLifeRecovery
{
    EffectClock? clock;
    public double Until => clock?.Deadline ?? 0;
    double last, rate, pending;
    public double Rate(double now) => now < Until ? rate : 0;
    public double RemainingBase(double now) { Integrate(now); return clock?.Remaining ?? 0; }
    public void Begin(double now, double seconds, double perSecond, double expiryRate = 1)
    {
        Integrate(now);
        last = now; clock = new(seconds, now, expiryRate); rate = Math.Max(0, perSecond);
    }
    public void SetExpiryRate(double now, double expiryRate)
    { Integrate(now); clock?.ChangeRate(now, expiryRate); }
    void Integrate(double now)
    {
        pending += Math.Max(0, Math.Min(now, Until) - last) * rate;
        clock?.Advance(now);
        last = Math.Max(now, last);
    }
    public double Take(double now)
    {
        Integrate(now); double result = pending; pending = 0; return result;
    }
    public void Clear() { clock = null; last = rate = pending = 0; }
    public readonly record struct Carried(double Remaining, double Rate, double Pending, double ExpiryRate = 1);
    public Carried Carry(double now)
    { Integrate(now); return new(clock?.Remaining ?? 0, rate, pending, clock?.Rate ?? 1); }
    public void Restore(Carried value, double now)
    { Clear(); clock = new(value.Remaining, now, value.ExpiryRate); last = now; rate = value.Rate; pending = value.Pending; }
}
