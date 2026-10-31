

/// <summary>
/// Connection-owned Fortification on a supplied game clock. Explicit grants expire independently; excess stacks
/// remain stored and can become effective after an earlier grant expires. Those lifetime/overflow choices are an
/// explicit LocalExile policy, not a claim that the unavailable official server algorithm has been reproduced.
/// </summary>
sealed class FortificationState
{
    public const int BaseMaximum = 20;
    public const double BaseDurationSeconds = 6;
    public const double HitGainIntervalSeconds = .1;
    public const string MaximumStat = "base_max_fortification";
    public const string DurationStat = "fortify_duration_+%";
    public const string LifeRegenPerStack = "life_regeneration_per_minute_%_per_fortification";
    public const string AttackSpeedPerStack = "attack_speed_+%_per_fortification";

    readonly List<Grant> grants = [];
    double lastTime = double.NegativeInfinity, nextHitGain = double.NegativeInfinity;
    int activeMaximum = BaseMaximum;
    public double StackSeconds { get; private set; }
    readonly record struct Grant(int Count, double ExpiresAt);
    public readonly record struct Carried(int Count, double Remaining);
    public sealed record Transfer(IReadOnlyList<Carried> Grants, double HitDelay);

    public readonly record struct Value(int Current, long Uncapped, int Maximum, double? NextExpiry)
    {
        public int LifeRegenerationPerMinutePercent(int perStack) => Product(Current, perStack);
        public int IncreasedAttackSpeedPercent(int perStack) => Product(Current, perStack);
        public double HitDamageFactor => Math.Max(0d, 1d - Current / 100d);
        static int Product(int count, int perStack) => (int)Math.Clamp((long)count * perStack, int.MinValue, int.MaxValue);
    }

    public static int Maximum(int addedMaximum) =>
        (int)Math.Clamp((long)BaseMaximum + addedMaximum, 0, int.MaxValue);

    public Value Read(double now, int addedMaximum = 0)
    {
        Advance(now);
        long uncapped = grants.Sum(g => (long)g.Count);
        int maximum = Maximum(addedMaximum);
        activeMaximum = maximum;
        return new((int)Math.Min(uncapped, maximum), uncapped, maximum,
            grants.Count == 0 ? null : grants.Min(g => g.ExpiresAt));
    }

    /// <summary>No hit damage formula is hidden here. The caller has explicitly supplied the number of stacks.</summary>
    public bool GrantExplicitStacks(double now, int count, int durationIncreasePercent = 0)
    {
        Advance(now);
        double duration = BaseDurationSeconds * Math.Max(0d, 1d + durationIncreasePercent / 100d);
        if (count <= 0 || duration <= 0) return false;
        grants.Add(new(count, now + duration));
        return true;
    }

    /// <summary>
    /// Applies the shared .1s hit-gain throttle to an already calculated count. A zero-stack result consumes no
    /// cooldown under the LocalExile model. Rejected/dead/stale contacts must be filtered by the caller before here.
    /// </summary>
    public bool TryGrantFromHit(double now, int calculatedCount, int durationIncreasePercent = 0)
    {
        Advance(now);
        // One ULP admits the representational .2 + .1 versus .3 boundary without a gameplay-sized tolerance.
        if (Math.BitIncrement(now) < nextHitGain || !GrantExplicitStacks(now, calculatedCount, durationIncreasePercent)) return false;
        nextHitGain = now + HitGainIntervalSeconds;
        return true;
    }

    public void Clear()
    {
        grants.Clear();
        lastTime = nextHitGain = double.NegativeInfinity;
        StackSeconds = 0;
        activeMaximum = BaseMaximum;
    }

    public Transfer Carry(double now)
    {
        Advance(now);
        return new(grants.Select(g => new Carried(g.Count, g.ExpiresAt - now)).ToArray(), Math.Max(0, nextHitGain - now));
    }

    public void Restore(Transfer carried, double now)
    {
        Clear();
        Advance(now);
        foreach (var grant in carried.Grants)
            if (grant.Count > 0 && double.IsFinite(grant.Remaining) && grant.Remaining > 0)
                grants.Add(new(grant.Count, now + grant.Remaining));
        nextHitGain = now + Math.Max(0, carried.HitDelay);
    }

    void Advance(double now)
    {
        if (!double.IsFinite(now) || now < 0 || now < lastTime) throw new ArgumentOutOfRangeException(nameof(now));
        if (double.IsFinite(lastTime))
        {
            double from = lastTime;
            foreach (double boundary in grants.Select(g => g.ExpiresAt).Where(t => t > from && t < now)
                .Distinct().Order().Append(now))
            {
                long count = grants.Where(g => g.ExpiresAt > from).Sum(g => (long)g.Count);
                StackSeconds += Math.Min(count, activeMaximum) * (boundary - from);
                from = boundary;
            }
        }
        lastTime = now;
        grants.RemoveAll(g => g.ExpiresAt <= now);
    }
}
