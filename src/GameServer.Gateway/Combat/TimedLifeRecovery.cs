/// <summary>A paid skill's finite recovery, integrated once on the owner's pausable clock.</summary>
sealed class TimedLifeRecovery
{
    public double Until { get; private set; }
    double last, rate, pending;
    public double Rate(double now) => now < Until ? rate : 0;
    public void Begin(double now, double seconds, double perSecond)
    {
        Integrate(now);
        last = now; Until = now + Math.Max(0, seconds); rate = Math.Max(0, perSecond);
    }
    void Integrate(double now)
    {
        pending += Math.Max(0, Math.Min(now, Until) - last) * rate;
        last = Math.Max(now, last);
    }
    public double Take(double now)
    {
        Integrate(now); double result = pending; pending = 0; return result;
    }
    public void Clear() { Until = last = rate = pending = 0; }
    public readonly record struct Carried(double Remaining, double Rate, double Pending);
    public Carried Carry(double now) { Integrate(now); return new(Math.Max(0, Until - now), rate, pending); }
    public void Restore(Carried value, double now)
    { Clear(); Until = now + value.Remaining; last = now; rate = value.Rate; pending = value.Pending; }
}
