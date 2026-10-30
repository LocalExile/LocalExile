using GameServer.Core.World;

/// <summary>
/// The player's ignites on one monster. Several can be on it at once; only the strongest deals damage, and each keeps its
/// own expiry, so a weaker, longer one resumes when the strongest ends (PoE 3.x ignite). Guarded by the connection's
/// Monsters lock; times are projectile-world seconds (<c>ProjectileWorldTime</c>), so pause freezes them.
/// </summary>
sealed class MonsterIgnites
{
    /// <summary>What a hit can ignite with, captured at release: the chance, the ignite's fire damage per second for a
    /// non-critical and a critical hit (the panel's ignite rows, <see cref="AilmentPanelStats"/>, already scaled by the
    /// damage-over-time buckets), and the duration in seconds.</summary>
    public readonly record struct Settings(int Chance, double Min, double Max, double CriticalMin, double CriticalMax,
        double Seconds)
    {
        public static readonly Settings None = new(0, 0, 0, 0, 0, 0);

        /// <summary><paramref name="rows"/>: a context's ailment rows (per minute); <paramref name="get"/>: the merged
        /// caster and skill stats. Base duration 4 s (<see cref="Ailments.IgniteDurationSeconds"/>); never_ignite and
        /// never_damaging_ailments stop it.</summary>
        public static Settings From(IEnumerable<(string Stat, int Value)> rows, string hand, Func<string, int> get)
        {
            if (get("never_ignite") != 0 || get("never_damaging_ailments") != 0) return None;
            var r = rows.ToDictionary(x => x.Stat, x => x.Value);
            double Row(string bound, string kind) =>
                r.GetValueOrDefault($"{bound}_ignite_damage_to_deal_per_minute_from_{kind}_{hand}_damage") / 60.0;
            long chance = (long)get("base_chance_to_ignite_%") + get("chance_to_freeze_shock_ignite_%");
            double seconds = Ailments.IgniteDurationSeconds
                * Math.Max(0, 1 + ((double)get("ignite_duration_+%") + get("base_all_ailment_duration_+%")) / 100);
            return new((int)Math.Clamp(chance, 0, 100), Row("minimum", "non_critical"), Row("maximum", "non_critical"),
                Row("minimum", "critical"), Row("maximum", "critical"), seconds);
        }
    }

    sealed class Ignite(double from, double remaining, double damagePerSecond)
    {
        public readonly double From = from, DamagePerSecond = damagePerSecond;
        public double Remaining = remaining;
    }
    readonly List<Ignite> active = [];
    double last, pending, rate = 1, rateUntil = double.PositiveInfinity;

    /// <summary>The native <c>ignited</c> buff instance on the monster while it burns (BuffDefinitions row 0).</summary>
    public ushort? Buff;

    public bool Burning => active.Count > 0;
    public bool HasPendingDamage => pending > 0;

    /// <summary>When the last of its ignites ends.</summary>
    public double Until => active.Count == 0 ? 0 : active.Max(i =>
    {
        double from = Math.Max(last, i.From), slowSeconds = Math.Max(0, rateUntil - from);
        return from + (i.Remaining <= slowSeconds * rate ? i.Remaining / rate
            : slowSeconds + i.Remaining - slowSeconds * rate);
    });

    // A recipient-rate change reconciles the previous interval, including a natural curse deadline even if the first
    // reader is late. Only Advance drains the accumulated, mitigated damage into the authoritative resource writer.
    public void ChangeRate(double now, double next, double deadline, double factor)
    {
        Accrue(now, factor);
        rate = deadline > now ? Math.Max(.25, next) : 1;
        rateUntil = rate == 1 ? double.PositiveInfinity : deadline;
    }

    /// <summary>Remaining BASE duration at publication, without advancing clocks or consuming any damage.</summary>
    public double Remaining(double now)
    {
        var copy = active.Select(i => new Ignite(i.From, i.Remaining, i.DamagePerSecond)).ToList();
        Integrate(copy, last, Math.Max(last, now), rate, rateUntil);
        return copy.Count == 0 ? 0 : copy.Max(i => i.Remaining);
    }

    /// <summary>A hit that dealt fire damage tries to ignite. Critical hits always do; otherwise the chance rolls, then the
    /// target's avoidance. The damage per second is an independent roll in the context's range, as the incoming
    /// ailments roll theirs (<c>ApplyPlayerAilmentsLocked</c>). True when an ignite was added.</summary>
    public bool TryApply(Settings settings, bool critical, double now, int avoid, bool immune, Random random)
    {
        if (immune || settings.Seconds <= 0 || !critical && !RandomRolls.Chance(settings.Chance, 100, random)
            || avoid > 0 && RandomRolls.Chance(avoid, 100, random)) return false;
        var (min, max) = critical ? (settings.CriticalMin, settings.CriticalMax) : (settings.Min, settings.Max);
        double perSecond = min + (Math.Max(min, max) - min) * random.NextDouble();
        if (!(perSecond > 0)) return false;
        if (active.Count == 0) last = Math.Max(last, now);
        active.Add(new(Math.Max(last, now), settings.Seconds, perSecond));
        return true;
    }

    /// <summary>The whole fire damage the strongest ignite deals from the last advance to <paramref name="until"/>,
    /// through every start and end in between, times the target's mitigation <paramref name="factor"/>; the fraction
    /// carries to the next advance. Ended ignites are dropped.</summary>
    public int Advance(double until, double factor)
    {
        Accrue(until, factor);
        int whole = (int)Math.Min(int.MaxValue, Math.Floor(Math.Max(0, pending)));
        pending = active.Count == 0 ? 0 : pending - whole;
        return whole;
    }

    void Accrue(double until, double factor)
    {
        if (!(until > last)) return;
        pending += Integrate(active, last, until, rate, rateUntil) * Math.Max(0, factor);
        last = until;
        if (until >= rateUntil) { rate = 1; rateUntil = double.PositiveInfinity; }
    }

    // The same strongest-only timeline drives real accrual and pure timer projection. Time-rate scales budgets, never
    // DPS or mitigation. Every source continues expiring while a stronger one burns, so fallback keeps its own budget.
    static double Integrate(List<Ignite> effects, double from, double until, double rate, double deadline)
    {
        double damage = 0;
        while (from < until && effects.Count > 0)
        {
            double elapsedRate = from < deadline ? rate : 1;
            double end = from < deadline ? Math.Min(until, deadline) : until;
            foreach (var i in effects)
                end = Math.Min(end, i.From > from ? i.From : from + i.Remaining / elapsedRate);
            double seconds = end - from;
            damage += effects.Where(i => i.From <= from).Select(i => i.DamagePerSecond).DefaultIfEmpty().Max() * seconds;
            foreach (var i in effects.Where(i => i.From <= from))
                i.Remaining = Math.Max(0, i.Remaining - seconds * elapsedRate);
            effects.RemoveAll(i => i.Remaining <= 1e-12);
            from = end;
        }
        return damage;
    }

    public void Clear() { active.Clear(); pending = 0; }
}
