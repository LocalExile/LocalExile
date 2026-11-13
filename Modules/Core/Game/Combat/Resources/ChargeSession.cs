using System.Collections.Concurrent;


/// <summary>
/// One connection's charges at work (issue #237): the events that may grant charges wait in a queue (the combat code
/// only enqueues, under any lock), and <see cref="Step"/>, run by the Gateway's charge ticker every
/// <see cref="StepSeconds"/> of game time, rolls them, times the periodic gains, and expires and clamps the charges.
/// The limits (maximum, minimum, duration) and the periodic stats are not polled: <see cref="Observe"/> takes them
/// whenever the character's stats are worked out (an item, a passive, an aura, /set_stat, a level all do that).
/// <see cref="Die"/> takes every gained charge at once. <see cref="Presentation"/> says which charge buffs the client
/// must get or lose. Not thread-safe: the Gateway calls it under the connection's Monsters lock.
/// </summary>
public sealed class ChargeSession(Charges.State? state = null, Action<ChargeType, double>? gained = null,
    Func<double>? clock = null, Func<double, double, double>? movingSeconds = null)
{
    /// <summary>How often the ticker steps (seconds): expiry and periodic gains are this precise.</summary>
    public const double StepSeconds = 0.05;

    /// <summary>PoE's "recently": the last 4 seconds.</summary>
    public const double RecentlySeconds = 4;

    public Charges.State State { get; } = state ?? new();
    public ChargeDegeneration Degeneration { get; } = new();
    public void SettleDegeneration(double now) => Degeneration.Advance(now, this, movingSeconds ?? ((_, _) => 0));
    readonly ConcurrentQueue<ChargeEvent> events = new();
    readonly ChargeGains.Timers timers = new();
    readonly Charges.Limits[] limits = [Default, Default, Default];
    readonly int[] applied = new int[3];
    Charges.Shown[] shown => State.Presented;
    Dictionary<string, int> observed = [];
    double lastStep = double.NaN;

    static readonly Charges.Limits Default = new(ChargePanelStats.BaseMaximumCharges, 0, Charges.BaseDurationMs);

    /// <summary>When a monster's hit last landed on the player (game clock seconds).</summary>
    public double LastHitAt { get; private set; } = double.NegativeInfinity;

    /// <summary>The type's limits as last observed.</summary>
    public Charges.Limits Limits(ChargeType type) => limits[(int)type];

    /// <summary>The charges the character has now (the minimum counts).</summary>
    public int Effective(ChargeType type) => State.Effective(type, limits[(int)type]);

    public void Enqueue(ChargeEvent e) => events.Enqueue(e);

    /// <summary>Events wait: the ticker works the character's stats out (outside the lock) before the next step.</summary>
    public bool Pending => !events.IsEmpty;

    /// <summary>
    /// The character's stats as they are now (<paramref name="get"/>: by Stats.dat Id): the limits of each type and
    /// the stats the session reads between events (the periodic gains, the gain-to-maximum chances, the duration
    /// stats), kept until the next observation.
    /// </summary>
    public void Observe(Func<string, int> get)
    {
        SettleDegeneration(clock?.Invoke() ?? (double.IsNaN(lastStep) ? 0 : lastStep));
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        int Record(string name) => seen[name] = get(name);
        foreach (var type in Charges.Types)
        {
            limits[(int)type] = Charges.LimitsOf(type, Record, State.Held(type));
            Record(ChargeGains.ToMaximumStat(type));
            Record(Charges.ConversionFlag(type));
            Record(Charges.MinimumConversionFlag(type));
        }
        foreach (var p in ChargeGains.Periodics) Record(p.Stat);
        observed = seen;
    }

    int Observed(string name) => observed.GetValueOrDefault(name);

    /// <summary>Whether the type's charges are its special type's (Brutal, Affliction, Absorption) as last observed:
    /// the "gain … instead of" flag (<see cref="Charges.ConversionFlag"/>).</summary>
    public bool Converted(ChargeType type) => Observed(Charges.ConversionFlag(type)) != 0;

    /// <summary>Whether the type's minimum applies to its special type instead, as last observed
    /// (<see cref="Charges.MinimumConversionFlag"/>).</summary>
    public bool MinimumConverted(ChargeType type) => Observed(Charges.MinimumConversionFlag(type)) != 0;

    /// <summary>The character died: every gained charge goes at once (the minimum stays), with the events still
    /// waiting, the periodic timers and "hit recently". True when a charge was held.</summary>
    public bool Die()
    {
        while (events.TryDequeue(out _)) { }
        timers.Reset();
        Degeneration.Clear(clock?.Invoke() ?? (double.IsNaN(lastStep) ? 0 : lastStep));
        LastHitAt = double.NegativeInfinity;
        return State.Clear();
    }

    /// <summary>What one step did: whether the charges the stats count changed, and lines for the log.</summary>
    public sealed record StepResult(bool CountsChanged, IReadOnlyList<string> Log);

    /// <summary>
    /// One step at <paramref name="now"/> (game clock seconds). <paramref name="stats"/>: the character's stats when
    /// events are waiting (their gain chances); without them the events wait for the next step. While dead every gained
    /// charge is lost and the events are dropped; in towns no periodic charge is gained.
    /// </summary>
    public StepResult Step(double now, Func<string, int>? stats, Func<WeaponProfile> weapon, bool alive, bool moving,
        bool inTown, Random rng) =>
        Step(now, stats, weapon, alive, moving, inTown, rng, mineDetonatedRecently: false);

    public StepResult Step(double now, Func<string, int>? stats, Func<WeaponProfile> weapon, bool alive, bool moving,
        bool inTown, Random rng, bool mineDetonatedRecently)
    {
        var log = new List<string>();
        double elapsed = double.IsNaN(lastStep) ? 0 : Math.Max(0, now - lastStep);
        lastStep = now;
        if (!alive)
        {
            if (Die()) log.Add("charges lost on death");
        }
        else
        {
            while (stats is not null && events.TryDequeue(out var e)) ApplyEvent(e, now, stats, weapon, rng, log);
            if (!inTown)
                foreach (var gain in timers.Advance(elapsed, Observed, now - LastHitAt <= RecentlySeconds, moving, rng,
                             mineDetonatedRecently))
                    Apply(gain, now, rng, log);
        }
        return Finish(now, log);
    }

    // An admitted contact resolves only its own events at the original contact time. It never steps periodic/moving
    // timers, drains unrelated queued events, or changes lastStep. Call in the same mutation transaction as the hit.
    public StepResult Admit(double now, IEnumerable<ChargeEvent> accepted, Func<string, int> stats,
        Func<WeaponProfile> weapon, bool alive, Random rng)
    {
        var log = new List<string>();
        if (alive) foreach (var e in accepted) ApplyEvent(e, now, stats, weapon, rng, log);
        return Finish(now, log);
    }

    // Current presentation can expire/clamp, but cannot integrate already processed time or reset a moving timer.
    public StepResult Refresh(double now) => Finish(double.IsNaN(lastStep) ? now : Math.Max(now, lastStep), []);

    /// <summary>Whether a charge of the type is held above the minimum (<c>current_removable_*_charges</c>).</summary>
    public bool CanSpend(ChargeType type) => Effective(type) > limits[(int)type].Minimum;

    /// <summary>Spends one charge of the type above the minimum (a cooldown bypass); the next step presents the count.
    /// False when none can be spent.</summary>
    public bool Spend(ChargeType type)
    {
        SettleDegeneration(clock?.Invoke() ?? (double.IsNaN(lastStep) ? 0 : lastStep));
        if (!CanSpend(type)) return false;
        var (_, left) = State.Lose(type, 1, limits[(int)type]);
        limits[(int)type] = Charges.LimitsOf(type, Observed, left);
        return true;
    }

    void ApplyEvent(ChargeEvent e, double now, Func<string, int> stats, Func<WeaponProfile> weapon, Random rng, List<string> log)
    {
        SettleDegeneration(now);
        if (e is ChargeEvent.WhenHit) { LastHitAt = now; Degeneration.HitUntil = now + RecentlySeconds; }
        if (e is ChargeEvent.Hit { Critical: true }) Degeneration.CritUntil = now + RecentlySeconds;
        if (e is ChargeEvent.Set set)
        {
            foreach (var type in set.Type is { } only ? [only] : Charges.Types)
                State.Set(type, set.Count, now, limits[(int)type]);
            log.Add(set.Type is { } t ? $"{Charges.Name(t)} charges set to {set.Count}" : "charges cleared");
            return;
        }
        foreach (var gain in ChargeGains.Roll(e, stats, weapon(), rng)) Apply(gain, now, rng, log);
    }

    StepResult Finish(double now, List<string> log)
    {
        SettleDegeneration(now);
        foreach (var type in State.Expire(now)) log.Add($"{Charges.Name(type)} charges expired");
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

    void Apply(ChargeGains.Gain gain, double now, Random rng, List<string> log)
    {
        var current = limits[(int)gain.Type];
        if (gain.Count < 0)
        {
            // A per-second loss (Replica Fragility, Replica Pacifism, Replica Powerlessness).
            var (lost, left) = State.Lose(gain.Type, -gain.Count, current);
            limits[(int)gain.Type] = Charges.LimitsOf(gain.Type, Observed, left);
            log.Add($"{gain.Source}: {Charges.Name(gain.Type)} charges {lost} -> {left}/{limits[(int)gain.Type].Maximum}");
            return;
        }
        int after = Math.Min(current.Maximum, State.Effective(gain.Type, current) + gain.Count);
        // The duration of charges counted per Frenzy Charge (frenzy_charge_duration_+%_per_frenzy_charge) uses the count
        // the gain leaves.
        var l = Charges.LimitsOf(gain.Type, Observed, after);
        limits[(int)gain.Type] = l;
        bool toMaximum = ChargeGains.Rolls(Observed(ChargeGains.ToMaximumStat(gain.Type)), rng);
        var (before, now2) = State.Gain(gain.Type, gain.Count, now, l, toMaximum);
        // A successful gain refreshes the type even at its cap. Minimums, direct sets and converted charges
        // cannot manufacture a standard-charge gain history.
        if (gain.Count > 0 && l.Maximum > 0 && !Converted(gain.Type)) gained?.Invoke(gain.Type, now);
        log.Add($"{gain.Source}: {Charges.Label(gain.Type, Converted(gain.Type))} charges {before} -> {now2}/{l.Maximum}" +
            (toMaximum ? " (to maximum)" : "") + $", {l.DurationMs / 1000.0:0.###} s");
    }

    /// <summary>A charge buff to send: the previous instance to remove (0: none) and, when <paramref name="Count"/> is
    /// above 0, the new one to add with its remaining seconds (null: no duration, the minimum charges).
    /// <paramref name="Converted"/>: the new one is the special type's buff (Brutal, Affliction, Absorption).</summary>
    public readonly record struct BuffChange(ChargeType Type, ushort Remove, ushort Add, int Count, double? Remaining, bool Converted = false);

    /// <summary>
    /// The charge buffs the client must get: for each type whose count, expiry or conversion changed since the last
    /// call, its old buff comes off and a new one goes on (<paramref name="nextBuff"/> numbers it) with the count and
    /// the duration left. Charges at the minimum do not expire, so they go without a duration. <paramref name="resend"/>:
    /// send every held type again (a new player object after an area change).
    /// </summary>
    public List<BuffChange> Presentation(double now, Func<ushort> nextBuff, bool resend = false)
    {
        var changes = new List<BuffChange>();
        foreach (var type in Charges.Types)
        {
            int i = (int)type;
            int count = Effective(type);
            bool timed = State.Held(type) > limits[i].Minimum;
            bool converted = Converted(type);
            double expires = timed ? State.ExpiresAt(type) : double.PositiveInfinity;
            var old = shown[i];
            if (!resend && old.Count == count && (count == 0 || old.ExpiresAt == expires && old.Converted == converted)) continue;
            if (count == 0 && old.Buff == 0) continue;
            ushort add = count > 0 ? nextBuff() : (ushort)0;
            changes.Add(new(type, old.Buff, add, count, timed ? Math.Max(0, expires - now) * State.ExpiryRate : null, converted));
            shown[i] = new(count, expires, add, converted);
        }
        return changes;
    }

    /// <summary>Forgets the buffs sent (a new player object has none).</summary>
    public void ForgetShown() => Array.Clear(shown);
}
