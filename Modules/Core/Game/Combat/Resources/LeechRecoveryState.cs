

/// <summary>One resource's timed leech instances. Call under the owner's combat lock with its game clock.
/// An instance stores its base rate/duration at creation; current total-recovery and cap modifiers apply outside
/// those instances. Settling before configuration changes prevents a new modifier from changing elapsed recovery.</summary>
sealed class LeechRecoveryState
{
    public readonly record struct Settings(double TotalMultiplier, double MaximumPerSecond, double RecoveryMultiplier);
    readonly record struct Instance(double Rate, bool Attack);
    readonly PriorityQueue<Instance, double> expiries = new();
    Settings settings;
    double at, baseRate, pending;
    bool initialized;
    int attackCount;

    public int Count => expiries.Count;
    public bool Active(double now) { Settle(now); return expiries.Count > 0; }

    public void Configure(double now, Settings value)
    {
        Settle(now);
        settings = new(Positive(value.TotalMultiplier), Positive(value.MaximumPerSecond), Positive(value.RecoveryMultiplier));
    }

    // Returns the instant portion. The remaining nominal amount determines duration, not a drainable recovery bank:
    // increased total recovery can recover more than that nominal amount without shortening the instance.
    public double Grant(double now, double amount, uint maximumPool, double maximumPerInstance, double instantPercent = 0,
        bool attack = false)
    {
        if (!double.IsFinite(now)) return 0;
        Settle(now);
        if (maximumPool == 0) return 0;
        amount = Math.Min(Positive(amount), Positive(maximumPerInstance));
        if (amount == 0) return 0;
        double instant = amount * Math.Clamp(double.IsFinite(instantPercent) ? instantPercent : 0, 0, 100) / 100;
        double remaining = amount - instant;
        if (remaining > 0)
        {
            double rate = maximumPool * .02;
            expiries.Enqueue(new(rate, attack), at + remaining / rate);
            if (attack) attackCount++;
            baseRate += rate;
        }
        return instant;
    }

    public double TakeRecovery(double now)
    {
        Settle(now);
        double value = pending;
        pending = 0;
        return value;
    }

    public void Clear(double now)
    {
        expiries.Clear();
        baseRate = pending = 0;
        attackCount = 0;
        if (double.IsFinite(now)) { at = now; initialized = true; }
    }

    // Full ES can remove spell instances while retaining attack instances. Retained rates/expiries are unchanged,
    // and all instances still use this one capped sum. Recovery accumulated at a full pool cannot be banked.
    public void ClearNonAttack(double now)
    {
        Settle(now);
        pending = 0;
        if (attackCount == expiries.Count) return;
        var retained = expiries.UnorderedItems.Where(i => i.Element.Attack).ToArray();
        expiries.Clear();
        expiries.EnqueueRange(retained);
        baseRate = retained.Sum(i => i.Element.Rate);
    }

    void Settle(double now)
    {
        if (!double.IsFinite(now)) return;
        if (!initialized) { at = now; initialized = true; }
        now = Math.Max(at, now);
        while (expiries.TryPeek(out var instance, out double expires) && expires <= now)
        {
            Accrue(Math.Max(at, expires));
            expiries.Dequeue();
            baseRate = Math.Max(0, baseRate - instance.Rate);
            if (instance.Attack) attackCount--;
        }
        if (expiries.Count == 0) baseRate = 0; // Remove accumulated floating-point residue after the last instance.
        Accrue(now);
    }

    void Accrue(double end)
    {
        pending += (end - at) * Math.Min(baseRate * settings.TotalMultiplier, settings.MaximumPerSecond)
            * settings.RecoveryMultiplier;
        at = end;
    }

    static double Positive(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
