/// <summary>Unmodified life leech for a summoned wolf: 2% maximum life/s per instance, 10% per hit and 20% total/s.
/// The per-instance rate is the PoB Data.misc.LeechRateBase; both caps are Monster.ot's values in 3.29.3b.</summary>
sealed class MinionLeech
{
    readonly List<(double Start, double End)> _instances = [];
    double _remainder;
    public void Clear() { _instances.Clear(); _remainder = 0; }
    public void Add(double now, uint maximum, uint current, double physicalDamage)
    {
        if (current >= maximum || maximum == 0 || physicalDamage <= 0) return;
        double amount = Math.Min(physicalDamage, maximum * .1); // SummonedWolfLifeLeech: 100% physical attack damage.
        _instances.Add((now, now + amount / (maximum * .02)));
    }
    public uint Recover(double from, double now, uint maximum, uint current)
    {
        if (current >= maximum || current == 0) { Clear(); return current; }
        if (now <= from || maximum == 0) return current;
        // Integrate through instance expiries so a delayed tick cannot recover after an instance ended.
        double start = from;
        foreach (double end in _instances.SelectMany(e => new[] { e.Start, e.End })
            .Where(e => e > from && e < now).Distinct().Order().Append(now))
        {
            int count = _instances.Count(e => e.Start <= start && e.End > start);
            _remainder += Math.Min(.2, count * .02) * maximum * (end - start);
            start = end;
        }
        _instances.RemoveAll(e => e.End <= now);
        uint recovered = (uint)Math.Floor(_remainder);
        _remainder -= recovered;
        uint result = (uint)Math.Min(maximum, (long)current + recovered);
        if (result == maximum) Clear();
        return result;
    }
}
