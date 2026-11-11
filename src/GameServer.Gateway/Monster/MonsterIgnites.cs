using GameServer.Core.World;

/// <summary>
/// The player's ignites on one monster. Several can be on it at once; only the strongest deals damage, and each keeps its
/// own expiry, so a weaker, longer one resumes when the strongest ends (PoE 3.x ignite). Guarded by the connection's
/// Monsters lock; times are projectile-world seconds (<c>ProjectileWorldTime</c>), so pause freezes them. Every source
/// adds here: a hit's chance (<see cref="TryApply"/>) or an ignite a granted skill worked out itself (<see cref="Add"/>,
/// Abberath's Fury).
/// </summary>
public sealed class MonsterIgnites
{
    /// <summary>What a hit can ignite with, captured at release: the chance, the ignite's fire damage per second for a
    /// non-critical and a critical hit (the panel's ignite rows, <see cref="AilmentPanelStats"/>, already scaled by the
    /// damage-over-time buckets), and the duration in seconds.</summary>
    /// <param name="CriticalChance">The chance on a critical strike, for an ailment that rolls it there too (bleed);
    /// a critical strike always ignites.</param>
    /// <param name="Stacks">Bleed only: how many of the strongest deal damage at once (Crimson Dance: 8).</param>
    /// <param name="Aggravate">Bleed only: the chance a hit (<paramref name="CriticalAggravate"/>: a critical strike)
    /// aggravates the bleeds already on its target; <paramref name="Aggravated"/>: its own bleed is aggravated too.</param>
    public readonly record struct Settings(int Chance, double Min, double Max, double CriticalMin, double CriticalMax,
        double Seconds, int CriticalChance = 0, int Stacks = 1, int Aggravate = 0, int CriticalAggravate = 0,
        bool Aggravated = false, int RupturePercent = 0, int FireExposure = 0, int Spread = 0, bool AllDamage = false)
    {
        public static readonly Settings None = new(0, 0, 0, 0, 0, 0);

        /// <summary>Whether a hit of <paramref name="hit"/> (rolled, before mitigation) can ignite: its fire damage, or any
        /// damage with "All Damage can Ignite" (<see cref="AllDamage"/>, Shaper of Flames).</summary>
        public bool CanIgnite(DamagePacket hit) =>
            AllDamage ? hit.Components.Any(c => c.Range.Max > 0) : hit[DamageType.Fire].Max > 0;

        /// <summary><paramref name="rows"/>: a context's ailment rows (per minute); <paramref name="get"/>: the merged
        /// caster and skill stats. Base duration 4 s (<see cref="Ailments.IgniteDurationSeconds"/>); the ignite
        /// prohibitions (<see cref="MonsterElementalAilments.CannotInflict"/>) and never_damaging_ailments stop it. faster_burn_% and damaging_ailments_deal_damage_+%_faster multiply the damage
        /// per second and divide the duration (wiki Ignite; PoB IgniteBurnFaster).</summary>
        public static Settings From(IEnumerable<(string Stat, int Value)> rows, string hand, Func<string, int> get)
        {
            if (MonsterElementalAilments.CannotInflict("ignite", get) || get("never_damaging_ailments") != 0) return None;
            var r = rows.ToDictionary(x => x.Stat, x => x.Value);
            double faster = Faster(get);
            if (faster <= 0) return None;
            double Row(string bound, string kind) =>
                r.GetValueOrDefault($"{bound}_ignite_damage_to_deal_per_minute_from_{kind}_{hand}_damage") / 60.0 * faster;
            // Shaper of Flames: "Hits always Ignite" (always_ignite).
            long chance = (long)get("base_chance_to_ignite_%") + get("chance_to_freeze_shock_ignite_%")
                + (get("always_ignite") != 0 ? 100 : 0);
            double seconds = Ailments.IgniteDurationSeconds
                * Math.Max(0, 1 + ((double)get("ignite_duration_+%") + get("base_all_ailment_duration_+%")) / 100) / faster
                * DurationMultiplier(get);
            return new((int)Math.Clamp(chance, 0, 100), Row("minimum", "non_critical"), Row("maximum", "non_critical"),
                Row("minimum", "critical"), Row("maximum", "critical"), seconds,
                FireExposure: get("ignites_apply_fire_resistance_+"), AllDamage: get("all_damage_can_ignite") != 0);
        }
    }

    /// <summary>The "deal damage faster" factor of the attacker's ignites (1 without one).</summary>
    public static double Faster(Func<string, int> get) =>
        Math.Max(0, 1 + ((double)get("faster_burn_%") + get("damaging_ailments_deal_damage_+%_faster")) / 100.0);

    /// <summary>The target's base_self_ignite_duration_-% (Fire and Ignite Resistant) on an ignite's duration.</summary>
    public static double SelfDuration(Func<string, int> target) =>
        Math.Max(0, 1 - target("base_self_ignite_duration_-%") / 100.0);

    /// <summary>Sadism's "Supported Skills have 70% less Duration" on their damaging ailments
    /// (support_faster_ailments_ailment_duration_+%_final). Its "deal damage faster" stat alone shortens the duration
    /// proportionally and raises the damage per second, so without this the ailment's total damage would not fall.</summary>
    public static double DurationMultiplier(Func<string, int> get) =>
        Math.Max(0, 1 + get("support_faster_ailments_ailment_duration_+%_final") / 100.0);

    // Spread: its proliferation radius in cells (0: it does not spread). Proliferated: the copy another monster's
    // spreading ignite puts on this one, set again every AI tick (<see cref="Proliferated"/>).
    readonly record struct Ignite(double From, double Until, double DamagePerSecond, bool Aggravated = false, int Spread = 0,
        bool Proliferated = false);
    readonly List<Ignite> active = [];
    // The spreading ignites it had when it died: a corpse goes on spreading them until they end (wiki Ignite
    // Proliferation Support: the aura continues through death on intact corpses). They deal no damage.
    List<Ignite> corpse = [];

    /// <summary>When an ignite was last applied (Controlled Blaze's "Enemy Ignited Recently" window).</summary>
    public double LastApplied { get; private set; }

    /// <summary>How many of the strongest deal damage at once: 1, or Crimson Dance's 8 for bleeds.</summary>
    public int Stacks { get; set; } = 1;

    /// <summary>Aggravates every bleed on the monster for its remaining duration (wiki Bleeding).</summary>
    public void Aggravate()
    {
        for (int i = 0; i < active.Count; i++) active[i] = active[i] with { Aggravated = true };
    }

    // The damage per second of the live instances: the Stacks strongest, plus aggravatedExtra × the strongest aggravated
    // one (an aggravated bleed's "as though moving" part, which does not stack with the moving bonus).
    double Dealing(List<Ignite> live, double aggravatedExtra) =>
        live.Select(i => i.DamagePerSecond).OrderDescending().Take(Math.Max(1, Stacks)).Sum()
        + aggravatedExtra * live.Where(i => i.Aggravated).Select(i => i.DamagePerSecond).DefaultIfEmpty().Max();
    double last, carry;
    double expiryRate = 1;
    public void SetExpiryRate(double now, double rate)
    {
        rate = Math.Max(.25, rate);
        for (int i = 0; i < active.Count; i++)
            active[i] = active[i] with { Until = RecipientEffectTime.RetimedDeadline(active[i].Until, now, expiryRate, rate) };
        expiryRate = rate;
    }
    public double RemainingBase(double now) => Math.Max(0, Until - now) * expiryRate;

    /// <summary>The native <c>ignited</c> buff instance on the monster while it burns (BuffDefinitions row 0).</summary>
    public ushort? Buff;

    /// <summary>The attacking object shown as the buff source (0 for generic self-created ailments).</summary>
    public uint BuffSource;

    /// <summary>Moves the active instances and integration state to a fresh owner without restarting either.</summary>
    public void MoveTo(MonsterIgnites target)
    {
        if (ReferenceEquals(this, target)) return;
        target.active.AddRange(active);
        target.last = last;
        target.carry = carry;
        target.expiryRate = expiryRate;
        target.Stacks = Stacks;
        target.LastApplied = LastApplied;
        target.DiedBurning = DiedBurning;
        target.BuffSource = BuffSource;
        target.Buff = null;

        active.Clear();
        last = 0;
        carry = 0;
        expiryRate = 1;
        Stacks = 1;
        LastApplied = 0;
        DiedBurning = false;
        BuffSource = 0;
        Buff = null;
    }

    public bool Burning => active.Count > 0;

    /// <summary>Nothing burns and no buff is shown.</summary>
    public bool Idle => active.Count == 0 && Buff is null;

    /// <summary>When the last of its ignites ends.</summary>
    public double Until => active.Count == 0 ? 0 : active.Max(i => i.Until);

    /// <summary>The strongest ignite's damage per second at <paramref name="now"/> (0 when none burns; the
    /// <see cref="Stacks"/> strongest together).</summary>
    public double PerSecond(double now) => Dealing(active.Where(i => i.From <= now && now < i.Until).ToList(), 0);

    /// <summary>A hit that dealt fire damage tries to ignite. Critical hits always do; otherwise the chance rolls, then the
    /// target's avoidance. The damage per second is an independent roll in the context's range, as the incoming
    /// ailments roll theirs (<c>ApplyPlayerAilmentsLocked</c>). True when an ignite was added.</summary>
    public bool TryApply(Settings settings, bool critical, double now, int avoid, bool immune, Random random)
        => TryApply(settings, critical, now, avoid, immune, random, out _);

    public bool TryApply(Settings settings, bool critical, double now, int avoid, bool immune, Random random,
        out double perSecond)
    {
        perSecond = 0;
        if (immune || settings.Seconds <= 0 || !critical && !RandomRolls.Chance(settings.Chance, 100, random)
            || avoid > 0 && RandomRolls.Chance(avoid, 100, random)) return false;
        var (min, max) = critical ? (settings.CriticalMin, settings.CriticalMax) : (settings.Min, settings.Max);
        perSecond = min + (Math.Max(min, max) - min) * random.NextDouble();
        return Add(now, perSecond, settings.Seconds, settings.Aggravated, settings.Spread);
    }

    /// <summary>An ignite already worked out (its chance rolled, its damage and duration final). True when added.</summary>
    public bool Add(double now, double perSecond, double seconds, bool aggravated = false, int spread = 0)
    {
        if (!(perSecond > 0) || !(seconds > 0)) return false;
        if (active.Count == 0) { last = now; carry = 0; }
        active.Add(new(now, now + seconds / expiryRate, perSecond, aggravated, Math.Max(0, spread)));
        LastApplied = now;
        return true;
    }

    /// <summary>The whole damage the strongest ignites deal (<see cref="Dealing"/>) from the last advance to
    /// <paramref name="until"/>, through every start and end in between, times the target's mitigation
    /// <paramref name="factor"/>; the fraction carries to the next advance. Ended ignites are dropped.</summary>
    public int Advance(double until, double factor, double aggravatedExtra = 0) =>
        Advance(until, factor, aggravatedExtra, out _);

    /// <summary>The same advance, also reporting the end of the last cut where the active set dealt positive damage:
    /// not the frame time, and null when no positive mitigated subinterval existed.</summary>
    public int Advance(double until, double factor, double aggravatedExtra, out double? lastDamageAt)
    {
        lastDamageAt = null;
        if (active.Count == 0 || !(until > last)) return 0;
        var cuts = active.SelectMany(i => new[] { i.From, i.Until }).Where(t => last < t && t < until)
            .Append(last).Append(until).Distinct().Order().ToArray();
        double damage = carry;
        for (int k = 1; k < cuts.Length; k++)
        {
            double mid = (cuts[k - 1] + cuts[k]) / 2;
            double seconds = cuts[k] - cuts[k - 1];
            double mitigated = Dealing(active.Where(i => i.From <= mid && mid < i.Until).ToList(), aggravatedExtra)
                * Math.Max(0, factor);
            if (mitigated > 0 && seconds > 0) lastDamageAt = cuts[k];
            damage += mitigated * seconds;
        }
        last = until;
        active.RemoveAll(i => i.Until <= until);
        int whole = (int)Math.Min(int.MaxValue, Math.Floor(Math.Max(0, damage)));
        carry = active.Count == 0 ? 0 : damage - whole;
        return whole;
    }

    /// <summary>Ends a dead monster's ignites. <paramref name="burnt"/>: its ignite killed it, so it died ignited even if
    /// that ignite ran out in the same advance.</summary>
    public void Clear(bool burnt = false)
    {
        DiedBurning |= burnt || Burning;
        if (active.Count > 0) corpse = active.Where(i => i.Spread > 0 && !i.Proliferated).ToList();
        active.Clear();
        carry = 0;
    }

    /// <summary>Drops a corpse's retained spreading ignites (shattered or consumed: nothing left to spread from).</summary>
    public void RetireCorpse() => corpse = [];

    /// <summary>Its own live ignites that spread (a corpse's included): each an aura of its own radius in cells.</summary>
    public IEnumerable<(double DamagePerSecond, double Until, int Radius)> Spreads(double now) =>
        (active.Count > 0 ? active : corpse).Where(i => i.Spread > 0 && !i.Proliferated && i.From <= now && now < i.Until)
            .Select(i => (i.DamagePerSecond, i.Until, i.Spread));

    /// <summary>The widest radius of its own ignites (0: none spreads), for the log.</summary>
    public int SpreadRadius => active.Where(i => !i.Proliferated).Select(i => i.Spread).DefaultIfEmpty().Max();

    /// <summary>Ignite proliferation (wiki Elemental proliferation): the strongest spreading ignite that reaches this
    /// monster burns it at the same damage per second until that one ends, like an aura, competing with its own ignites
    /// (only the strongest deals damage). The copy does not spread and is not a new ignite (LastApplied stays). Null takes
    /// it off: the old copy ends at <paramref name="now"/>, so the next advance still counts it up to then. True when a
    /// new copy started.</summary>
    public bool Proliferated((double DamagePerSecond, double Until)? source, double now)
    {
        int at = active.FindIndex(i => i.Proliferated && i.Until > now);
        if (at >= 0)
        {
            var current = active[at];
            if (source is { } same && same.DamagePerSecond == current.DamagePerSecond && same.Until == current.Until)
                return false;
            active[at] = current with { Until = now };
        }
        if (source is not { } s || !(s.DamagePerSecond > 0) || !(s.Until > now)) return false;
        if (active.Count == 0) { last = now; carry = 0; }
        active.Add(new(now, s.Until, s.DamagePerSecond, Proliferated: true));
        return true;
    }

    /// <summary>It was ignited when it died (its ignites were cleared): the kill was of an ignited enemy.</summary>
    public bool DiedBurning { get; private set; }
}
