/// <summary>
/// Each impale stores a share of the actual physical hit before defender mitigation. A later hit of any type
/// triggers one separate reflected physical hit from all live impales. Instances expire independently by time
/// or remaining hit count; there is no separate five-stack cap. Callers hold the target's state lock.
/// </summary>
sealed class ImpaleState
{
    public const int HitsPerStack = 5;
    public const double BaseDuration = 8;
    public const double StoredShare = 0.10;

    readonly List<(double Stored, int HitsLeft, double ExpiresAt)> _stacks = [];

    public int Stacks => _stacks.Count;

    /// <summary>Stored physical damage after the most recent expiry/consumption observation.</summary>
    public double Bonus => _stacks.Sum(s => s.Stored);

    public void Expire(double now) => _stacks.RemoveAll(s => s.ExpiresAt <= now);

    /// <summary>Store the rolled physical damage including the inflicting critical multiplier, before target mitigation.
    /// Effect, hit count and expiry are captured at application; subsequent source changes do not rewrite an instance.</summary>
    public bool Apply(double physicalDamage, double now = 0, int effectPercent = 0,
        int hits = HitsPerStack, double duration = BaseDuration)
    {
        Expire(now);
        double stored = physicalDamage * StoredShare * Math.Max(0, 1 + effectPercent / 100.0);
        if (!double.IsFinite(stored) || stored <= 0 || hits <= 0 || !double.IsFinite(duration) || duration <= 0) return false;
        _stacks.Add((stored, hits, now + duration));
        return true;
    }

    /// <summary>
    /// A hit has landed, so every impale already on the target lasts one hit fewer; expired ones drop off.
    /// Call this before <see cref="Apply"/> for the same hit, or the new stack would lose one of its five hits.
    /// </summary>
    public void AfterHit(double now = 0)
    {
        Expire(now);
        for (int i = _stacks.Count - 1; i >= 0; i--)
        {
            var stack = _stacks[i];
            if (stack.HitsLeft <= 1) _stacks.RemoveAt(i);
            else _stacks[i] = (stack.Stored, stack.HitsLeft - 1, stack.ExpiresAt);
        }
    }

    public (double Reflected, bool Applied) OnHit(double physicalDamage, double now, ImpaleStats.Settings settings, Random random,
        int avoidChance = 0)
    {
        Expire(now);
        double reflected = Bonus;
        AfterHit(now);
        bool applied = physicalDamage > 0 && settings.Chance > 0 && random.Next(100) < settings.Chance
            && (avoidChance <= 0 || random.Next(100) >= Math.Clamp(avoidChance, 0, 100))
            && Apply(physicalDamage, now, settings.EffectPercent, settings.Hits, settings.Duration);
        return (reflected, applied);
    }

    public void Clear() => _stacks.Clear();
}
