// Standard-charge foundation adapted from PR #241 (85fffc1154797a97b0a09a939257abad98d791cc).
using System.Collections.Frozen;

/// <summary>
/// One connection's charge state. Combat enqueues only accepted events after the authoritative hit, kill or block
/// commits, capturing the event's game-clock time, stats and active weapon. Step never re-reads combat sources.
/// Every operation, including enqueue, is called under the connection's Monsters lock.
/// </summary>
sealed class ChargeSession
{
    public const double StepSeconds = 0.05;
    public const double RecentlySeconds = 4;

    public Charges.State State { get; } = new();
    readonly Queue<AcceptedEvent> events = new();
    readonly ChargeGains.Timers timers = new();
    readonly Charges.Limits[] limits = [Default, Default, Default];
    readonly int[] applied = new int[3];
    readonly Shown[] shown = new Shown[3];
    Dictionary<string, int> observed = [];
    double lastStep = double.NaN;
    double acceptedThrough = double.NegativeInfinity;
    bool closed;

    static readonly Charges.Limits Default = new(ChargePanelStats.BaseMaximumCharges, 0, Charges.BaseDurationMs);
    static readonly string[] EventInputs = ChargeGains.Rules.Select(r => r.Stat)
        .Concat(Charges.LimitInputs).Concat(Charges.Types.Select(ChargeGains.ToMaximumStat))
        .Distinct(StringComparer.Ordinal).ToArray();

    sealed record AcceptedEvent(ChargeEvent Event, double At, FrozenDictionary<string, int> Stats, WeaponProfile Weapon)
    {
        public int Get(string name) => Stats.GetValueOrDefault(name);
    }

    public double LastHitAt { get; private set; } = double.NegativeInfinity;
    public Charges.Limits Limits(ChargeType type) => limits[(int)type];
    public int Effective(ChargeType type) => State.Effective(type, limits[(int)type]);
    public bool Pending => events.Count != 0;
    public bool Closed => closed;

    /// <summary>
    /// Captures a committed event immediately. The caller supplies the active weapon, never both weapon sets.
    /// Stats and any skill-stat delegate are copied now; refunding a passive or swapping later cannot rewrite them.
    /// Closed/older sessions reject late events. Equal timestamps are permitted for several targets of one hit.
    /// </summary>
    public bool EnqueueAccepted(ChargeEvent e, double at, Func<string, int> getAtEvent, WeaponProfile activeWeapon)
    {
        if (closed || !double.IsFinite(at) || at < acceptedThrough || (!double.IsNaN(lastStep) && at < lastStep)) return false;
        var stats = EventInputs.ToFrozenDictionary(name => name, getAtEvent, StringComparer.Ordinal);
        if (e is ChargeEvent.Hit { Skill: { } skill } hit)
        {
            var skillStats = ChargeGains.Rules.Select(r => r.Stat).Distinct(StringComparer.Ordinal)
                .ToFrozenDictionary(name => name, skill, StringComparer.Ordinal);
            e = hit with { Skill = name => skillStats.GetValueOrDefault(name) };
        }
        events.Enqueue(new(e, at, stats, activeWeapon));
        acceptedThrough = at;
        return true;
    }

    /// <summary>Updates current limits and periodic sources when the character's stats change.</summary>
    public void Observe(Func<string, int> get)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        int Record(string name) => seen[name] = get(name);
        foreach (var type in Charges.Types)
        {
            limits[(int)type] = Charges.LimitsOf(type, Record, State.Held(type));
            Record(ChargeGains.ToMaximumStat(type));
        }
        foreach (var p in ChargeGains.Periodics) Record(p.Stat);
        observed = seen;
    }

    int Observed(string name) => observed.GetValueOrDefault(name);

    /// <summary>Death drops gains and pending events immediately, even if the game clock is paused.</summary>
    public bool Die(double now)
    {
        events.Clear();
        timers.Reset();
        LastHitAt = double.NegativeInfinity;
        if (double.IsFinite(now))
        {
            acceptedThrough = Math.Max(acceptedThrough, now);
            lastStep = double.IsNaN(lastStep) ? now : Math.Max(lastStep, now);
        }
        return State.Clear();
    }

    public sealed record StepResult(bool CountsChanged, IReadOnlyList<string> Log);

    /// <summary>
    /// Applies queued events at their original timestamps, expiring old charges before each gain. It then expires
    /// to now and applies current maximums. Pausing the game clock cannot advance expiry or periodic gains.
    /// </summary>
    public StepResult Step(double now, bool alive, bool moving, bool inTown, Random rng)
    {
        var log = new List<string>();
        if (closed || !double.IsFinite(now) || (!double.IsNaN(lastStep) && now < lastStep)) return new(false, log);
        double cursor = double.IsNaN(lastStep) ? (events.TryPeek(out var first) ? Math.Min(first.At, now) : now) : lastStep;
        if (!alive)
        {
            if (Die(now)) log.Add("charges lost on death");
        }
        else
        {
            while (events.TryPeek(out var waiting) && waiting.At <= now)
            {
                var e = events.Dequeue();
                AdvancePeriodic(cursor, e.At, moving, inTown, rng, log);
                cursor = e.At;
                Expire(e.At, log);
                if (e.Event is ChargeEvent.WhenHit) LastHitAt = e.At;
                if (e.Event is ChargeEvent.Set set)
                {
                    foreach (var type in set.Type is { } only ? [only] : Charges.Types)
                        State.Set(type, set.Count, e.At, Charges.LimitsOf(type, e.Get, Math.Max(0, set.Count)));
                    log.Add(set.Type is { } t ? $"{Charges.Name(t)} charges set to {set.Count}" : "charges cleared");
                }
                else
                    foreach (var gain in ChargeGains.Roll(e.Event, e.Get, e.Weapon, rng)) Apply(gain, e.At, e.Get, rng, log);
            }
            AdvancePeriodic(cursor, now, moving, inTown, rng, log);
            Expire(now, log);
        }
        lastStep = now;
        foreach (var type in Charges.Types)
            if (State.Clamp(type, limits[(int)type])) log.Add($"{Charges.Name(type)} charges down to the maximum {limits[(int)type].Maximum}");
        bool changed = false;
        foreach (var type in Charges.Types)
        {
            int count = Effective(type);
            if (count != applied[(int)type]) { applied[(int)type] = count; changed = true; }
        }
        return new(changed, log);
    }

    void AdvancePeriodic(double from, double to, bool moving, bool inTown, Random rng, List<string> log)
    {
        if (inTown || to <= from) return;
        // A hit at the end of a tick cannot enable gains before it; split at the four-second recent-hit deadline.
        double split = Math.Clamp(LastHitAt + RecentlySeconds, from, to);
        if (split > from) Advance(from, split, hitRecently: true);
        if (to > split) Advance(split, to, hitRecently: false);

        void Advance(double start, double end, bool hitRecently)
        {
            foreach (var gain in timers.AdvanceTimed(end - start, Observed, hitRecently, moving, rng))
            {
                double at = start + gain.Offset;
                Expire(at, log);
                Apply(gain.Gain, at, Observed, rng, log);
            }
        }
    }

    void Expire(double now, List<string> log)
    {
        foreach (var type in State.Expire(now)) log.Add($"{Charges.Name(type)} charges expired");
    }

    void Apply(ChargeGains.Gain gain, double now, Func<string, int> get, Random rng, List<string> log)
    {
        var current = Charges.LimitsOf(gain.Type, get, State.Held(gain.Type));
        int after = (int)Math.Clamp((long)State.Effective(gain.Type, current) + gain.Count, 0, current.Maximum);
        var l = Charges.LimitsOf(gain.Type, get, after);
        bool toMaximum = ChargeGains.Rolls(get(ChargeGains.ToMaximumStat(gain.Type)), rng);
        if (toMaximum) l = Charges.LimitsOf(gain.Type, get, l.Maximum);
        var (before, now2) = State.Gain(gain.Type, gain.Count, now, l, toMaximum);
        log.Add($"{gain.Source}: {Charges.Name(gain.Type)} charges {before} -> {now2}/{l.Maximum}" +
            (toMaximum ? " (to maximum)" : "") + $", {l.DurationMs / 1000.0:0.###} s");
    }

    public readonly record struct TransferredCharge(ChargeType Type, int Held, DateTimeOffset ExpiresAt);

    /// <summary>One-use transfer lease: only the first destination can take these charges.</summary>
    public sealed class Transfer(IReadOnlyList<TransferredCharge> entries)
    {
        readonly TransferredCharge[] charges = [.. entries];
        int consumed;
        internal bool TryTake(out IReadOnlyList<TransferredCharge> result)
        {
            result = charges;
            return Interlocked.Exchange(ref consumed, 1) == 0;
        }
    }

    /// <summary>
    /// Flushes accepted events before closing the old area. UTC deadlines deduct loading time, while normal gameplay
    /// uses the pausable game clock. A closed session cannot accept or apply late events.
    /// </summary>
    public Transfer CloseAndCarry(double now, DateTimeOffset utcNow, bool alive, bool moving, bool inTown, Random rng)
    {
        if (closed) return new([]);
        if (!double.IsFinite(now) || (!double.IsNaN(lastStep) && now < lastStep))
            throw new ArgumentOutOfRangeException(nameof(now));
        Step(now, alive, moving, inTown, rng);
        closed = true;
        var carry = State.Carry(now).Select(c => new TransferredCharge(c.Type, c.Held, utcNow.AddSeconds(c.Remaining))).ToArray();
        events.Clear();
        return new(carry);
    }

    /// <summary>Restores the still-live part once, clamped to the destination's observed maximums.</summary>
    public bool Restore(Transfer transfer, double now, DateTimeOffset utcNow)
    {
        if (closed || !double.IsFinite(now) || (!double.IsNaN(lastStep) && now < lastStep) || !transfer.TryTake(out var carry)) return false;
        State.Restore(carry.Where(c => c.ExpiresAt > utcNow)
            .Select(c => new Charges.Carried(c.Type, c.Held, (c.ExpiresAt - utcNow).TotalSeconds)), now);
        foreach (var type in Charges.Types) State.Clamp(type, limits[(int)type]);
        lastStep = now;
        acceptedThrough = now;
        ForgetShown();
        return true;
    }

    public readonly record struct Shown(int Count, double ExpiresAt, ushort Buff);
    public readonly record struct BuffChange(ChargeType Type, ushort Remove, ushort Add, int Count, double? Remaining);

    /// <summary>Returns the buffs to replace. The caller supplies the connection's atomic buff allocator.</summary>
    public List<BuffChange> Presentation(double now, Func<ushort> nextBuff, bool resend = false)
    {
        var changes = new List<BuffChange>();
        foreach (var type in Charges.Types)
        {
            int i = (int)type;
            int count = Effective(type);
            bool timed = State.Held(type) > limits[i].Minimum;
            double expires = timed ? State.ExpiresAt(type) : double.PositiveInfinity;
            var old = shown[i];
            if (!resend && old.Count == count && (count == 0 || old.ExpiresAt == expires)) continue;
            if (count == 0 && old.Buff == 0) continue;
            ushort add = count > 0 ? nextBuff() : (ushort)0;
            changes.Add(new(type, old.Buff, add, count, timed ? Math.Max(0, expires - now) : null));
            shown[i] = new(count, expires, add);
        }
        return changes;
    }

    public void ForgetShown() => Array.Clear(shown);
}
