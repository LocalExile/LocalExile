/// <summary>Recoup: each hit's share of the damage taken, recovered evenly over its own seconds after the hit. The
/// hits overlap, so every one keeps its own end and how far it has been paid.</summary>
sealed class RecoupRecovery
{
    sealed class Hit(double start, double until, double rate)
    {
        public readonly double Start = start, Until = until, Rate = rate;
        public double Paid = start;
    }

    readonly List<Hit> hits = [];

    public void Add(double now, double amount, double seconds)
    {
        if (amount > 0 && seconds > 0) hits.Add(new(now, now + seconds, amount / seconds));
    }

    /// <summary>What every running hit recovers up to <paramref name="now"/>; ended hits are dropped.</summary>
    public double Take(double now)
    {
        double total = 0;
        foreach (var hit in hits)
        {
            double to = Math.Min(now, hit.Until);
            if (to <= hit.Paid) continue;
            total += (to - hit.Paid) * hit.Rate;
            hit.Paid = to;
        }
        hits.RemoveAll(h => h.Paid >= h.Until);
        return total;
    }

    public void Clear() => hits.Clear();
}
