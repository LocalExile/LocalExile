/// <summary>A transient Rage balance advanced only by the caller's game clock. The caller serializes access.
/// Character.ot supplies 30 maximum and a 2000 ms loss delay; BuffDefinitions 1060 loses one Rage per 0.1 s.
/// Client 3.29.3b 0x142877ac0 divides the delay by its recovery rate. No stat getters or wall clocks run here.</summary>
sealed class RageState
{
    public readonly record struct Inputs(int MaximumBonus = 0, bool HalfMaximum = false, int EffectPercent = 0,
        int LossDelayMilliseconds = 0, int LossDelayRecoveryPercent = 0, bool NoInherentLoss = false,
        bool CannotGain = false, bool CannotGainDuringSoulGainPrevention = false,
        int MaximumOnHitGainChance = 0, int GainOnHit = 0, int GainOnAttackHit = 0,
        int GainOnMeleeHit = 0, int GainOnAttackCritical = 0, int GainOnMeleeCritical = 0, int GainWhenHit = 0,
        int LossRatePercent = 0)
    {
        public int Maximum
        {
            get
            {
                long maximum = Math.Clamp(30L + MaximumBonus, 0, int.MaxValue);
                return (int)(HalfMaximum ? maximum / 2 : maximum);
            }
        }

        // A nonpositive recovery rate stalls this timer. The native signed division does not define a useful
        // zero-denominator case; callers must not turn that invalid input into immediate resource loss.
        public double LossDelaySeconds
        {
            get
            {
                long rate = 100L + LossDelayRecoveryPercent;
                return rate > 0 ? Math.Max(0L, 2000L + LossDelayMilliseconds) / (10.0 * rate)
                    : double.PositiveInfinity;
            }
        }

        // rage_decay_speed_+% changes the loss rate. It is separate from recovery of the delay above.
        public double LossPerSecond => 10 * Math.Max(0, 1 + LossRatePercent / 100.0);
    }

    public readonly record struct Value(int Current, int Maximum, int Effective);
    // Version 1 policy: only a positive accepted gain refreshes activity and consumes the hit interval.
    // A capped attempted gain does neither. Official capped-attempt behavior is still unverified.
    public const int PolicyVersion = 1;
    public sealed record Carried(int Current, double? ActivityAge, double? HitAge, double LossProgress, Inputs Previous);
    int current;
    double? advancedAt, activityAt, hitGainAt;
    double lossProgress;
    Inputs previous;

    public Value Read(double now, Inputs inputs)
    {
        Advance(now, inputs);
        int effective = (int)Math.Clamp(decimal.Truncate(current * Math.Max(0m, 100m + inputs.EffectPercent) / 100),
            0, int.MaxValue);
        return new(current, inputs.Maximum, effective);
    }

    /// <summary>Positive gains pass both prohibitions. A failed/zero/capped gain does not reset activity.
    /// Resetting at a capped attempted gain is not established by primary evidence, so it is not assumed here.</summary>
    public bool Gain(long amount, double now, Inputs inputs, bool soulGainPrevention = false)
    {
        now = Advance(now, inputs);
        if (amount <= 0 || Blocked(inputs, soulGainPrevention)) return false;
        int added = (int)Math.Min(amount, (long)inputs.Maximum - current);
        if (added <= 0) return false;
        current += added;
        Activity(now);
        return true;
    }

    public bool Hit(double now, Inputs inputs, bool attack, bool melee, bool critical, int maximumChanceRoll,
        bool soulGainPrevention = false)
    {
        long amount = inputs.GainOnHit + (attack ? (long)inputs.GainOnAttackHit : 0)
            + (melee ? (long)inputs.GainOnMeleeHit : 0)
            + (attack && critical ? (long)inputs.GainOnAttackCritical : 0)
            + (melee && critical ? (long)inputs.GainOnMeleeCritical : 0);
        return GainFromHit(amount, now, inputs, maximumChanceRoll, soulGainPrevention);
    }

    /// <summary>Being hit resets inherent-loss activity even without a grant, including during prevention.
    /// The caller excludes evaded events; deciding which incoming events count as hits belongs to combat.</summary>
    public bool EnemyHit(double now, Inputs inputs, int maximumChanceRoll, bool soulGainPrevention = false)
    {
        now = Advance(now, inputs);
        Activity(now);
        return GainFromHit(inputs.GainWhenHit, now, inputs, maximumChanceRoll, soulGainPrevention);
    }

    bool GainFromHit(long amount, double now, Inputs inputs, int maximumChanceRoll, bool soulGainPrevention)
    {
        now = Advance(now, inputs);
        if (amount <= 0 || Blocked(inputs, soulGainPrevention)
            || hitGainAt is double last && now - last < 0.5) return false;
        int chance = Math.Clamp(inputs.MaximumOnHitGainChance, 0, 100);
        if (chance > 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumChanceRoll);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(maximumChanceRoll, 100);
            if (maximumChanceRoll < chance) amount = inputs.Maximum;
        }
        if (!Gain(amount, now, inputs, soulGainPrevention)) return false;
        hitGainAt = now;
        return true;
    }

    /// <summary>One up-front payment. Prevention forbids gains, not spending. Insufficient payments change nothing
    /// beyond settling elapsed loss; an accepted payment does not postpone inherent loss.</summary>
    public bool Spend(int amount, double now, Inputs inputs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        Advance(now, inputs);
        if (current < amount) return false;
        current -= amount;
        return true;
    }

    static bool Blocked(Inputs inputs, bool prevention) => inputs.CannotGain
        || prevention && inputs.CannotGainDuringSoulGainPrevention;

    void Activity(double now) { activityAt = now; lossProgress = 0; }

    double Advance(double now, Inputs inputs)
    {
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        if (advancedAt is double last)
        {
            now = Math.Max(last, now);
            if (!previous.NoInherentLoss && current > 0 && activityAt is double active)
            {
                double from = Math.Max(last, active + previous.LossDelaySeconds);
                if (now > from)
                {
                    double units = lossProgress + (now - from) * previous.LossPerSecond;
                    int lost = (int)Math.Min(current, Math.Floor(units + 1e-9));
                    current -= lost;
                    lossProgress = current > 0 ? units - lost : 0;
                }
            }
        }
        current = Math.Min(current, inputs.Maximum);
        previous = inputs;
        advancedAt = now;
        return now;
    }

    public void Clear()
    {
        current = 0;
        advancedAt = activityAt = hitGainAt = null;
        lossProgress = 0;
        previous = default;
    }

    public Carried Carry(double now, Inputs inputs)
    {
        now = Advance(now, inputs);
        var carried = new Carried(current, activityAt is double activity ? now - activity : null,
            hitGainAt is double hit ? now - hit : null, lossProgress, previous);
        Clear();
        return carried;
    }

    public void Restore(Carried carried, double now)
    {
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        current = carried.Current;
        activityAt = carried.ActivityAge is double activity ? now - activity : null;
        hitGainAt = carried.HitAge is double hit ? now - hit : null;
        lossProgress = carried.LossProgress;
        previous = carried.Previous;
        advancedAt = now;
    }
}
