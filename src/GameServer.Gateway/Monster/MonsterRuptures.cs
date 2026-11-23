/// <summary>
/// The Ruptures a player's critical bleeding hits have put on one monster (Rupture Support): up to four, each lasting
/// three seconds, and each raising the damage the monster takes from bleeding by the support's share.
/// Owned by the area's frame; times are projectile-world seconds.
/// </summary>
public sealed class MonsterRuptures
{
    public const int Maximum = 4;
    public const double Seconds = 3;

    public sealed record Rupture(long Id, double Until, int Percent);
    readonly List<Rupture> active = [];
    long nextId;
    double expiryRate = 1;

    /// <summary>The surviving independent timers; settle bleeding before pruning them.</summary>
    public IReadOnlyList<Rupture> Active => active;

    /// <summary>Rupture is a recipient debuff (BuffDefinitions category 2), so its own timer is slowed too.</summary>
    public void SetExpiryRate(double now, double rate)
    {
        rate = Math.Max(.25, rate);
        for (int i = 0; i < active.Count; i++)
            active[i] = active[i] with { Until = RecipientEffectTime.RetimedDeadline(active[i].Until, now, expiryRate, rate) };
        expiryRate = rate;
    }

    /// <summary>The next change in the active set, before pruning, so a caller can settle elapsed effects first.</summary>
    public double NextExpiry => active.Count == 0 ? double.PositiveInfinity : active.Min(r => r.Until);

    public int Stacks(double now)
    {
        active.RemoveAll(r => now >= r.Until);
        return active.Count;
    }

    /// <summary>The more multiplier the live Ruptures give bleeding: 1 + the support's share per stack.</summary>
    public double More(double now) => Stacks(now) == 0 ? 1 : 1 + active.Sum(r => (double)r.Percent) / 100.0;

    /// <summary>Adds one Rupture, replacing the one that ends first when there are already four.</summary>
    public void Add(double now, int percent)
    {
        Stacks(now);
        if (active.Count >= Maximum) active.RemoveAt(active.FindIndex(r => r.Until == active.Min(x => x.Until)));
        active.Add(new(++nextId, now + Seconds / expiryRate, percent));
    }

    public void Clear() => active.Clear();
}
