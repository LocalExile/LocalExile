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
    public readonly record struct DamageRanges(double Min, double Max, double CriticalMin, double CriticalMax)
    {
        public DamageRanges Scale(double factor) => new(Min * factor, Max * factor, CriticalMin * factor, CriticalMax * factor);
    }

    /// <summary>What a hit can ignite with, captured at release: the chance, the ignite's fire damage per second for a
    /// non-critical and a critical hit (the panel's ignite rows, <see cref="AilmentPanelStats"/>, already scaled by the
    /// damage-over-time buckets), and the duration in seconds.</summary>
    /// <param name="CriticalChance">The chance on a critical strike, for an ailment that rolls it there too (bleed);
    /// a critical strike always ignites.</param>
    /// <param name="Stacks">How many of the strongest instances may deal damage together.</param>
    /// <param name="Aggravate">Bleed only: the chance a hit (<paramref name="CriticalAggravate"/>: a critical strike)
    /// aggravates the bleeds already on its target; <paramref name="Aggravated"/>: its own bleed is aggravated too.</param>
    public readonly record struct Settings(int Chance, double Min, double Max, double CriticalMin, double CriticalMax,
        double Seconds, int CriticalChance = 0, int Stacks = 1, int Aggravate = 0, int CriticalAggravate = 0,
        bool Aggravated = false, int RupturePercent = 0, int FireExposure = 0, int Spread = 0, bool AllDamage = false,
        // Elemental Overload: "Ailments never count as being from Critical Strikes" — a critical strike rolls the hit's
        // chance and deals the non-critical damage, as a non-critical hit does.
        bool NeverFromCrit = false,
        // Perfect Agony: "Non-Critical Strikes cannot inflict Ailments". With Elemental Overload (NeverFromCrit) every
        // hit counts as non-critical for ailments, so the two together stop hits applying ailments at all.
        bool OnlyFromCrit = false)
    {
        public static readonly Settings None = new(0, 0, 0, 0, 0, 0);

        /// <summary>Whether a hit of <paramref name="hit"/> (rolled, before mitigation) can ignite: its fire damage, or any
        /// damage with "All Damage can Ignite" (<see cref="AllDamage"/>, Shaper of Flames).</summary>
        public bool CanIgnite(DamagePacket hit)
        {
            if (!AllDamage)
                return (!FireCannotIgnite && hit[DamageType.Fire].Max > 0) || (ColdCanIgnite && hit[DamageType.Cold].Max > 0);
            foreach (var type in DamageTypes.ConversionOrder)
                if ((type != DamageType.Fire || !FireCannotIgnite) && hit[type].Max > 0) return true;
            return false;
        }

        public bool FireCannotIgnite { get; init; }
        public bool ColdCanIgnite { get; init; }
        public bool CriticalNeedsIgniteChance { get; init; }
        public DamageRanges? ChilledTarget { get; init; }
        public DamageRanges Ranges => new(Min, Max, CriticalMin, CriticalMax);

        public Settings WithRanges(DamageRanges range) => this with
        {
            Min = range.Min, Max = range.Max, CriticalMin = range.CriticalMin, CriticalMax = range.CriticalMax,
        };

        public Settings ScaleDamage(double factor) =>
            WithRanges(Ranges.Scale(factor)) with { ChilledTarget = ChilledTarget?.Scale(factor) };

        /// <summary><paramref name="rows"/>: a context's ailment rows (per minute); <paramref name="get"/>: the merged
        /// caster and skill stats. Base duration 4 s (<see cref="Ailments.IgniteDurationSeconds"/>); the ignite
        /// prohibitions (<see cref="MonsterElementalAilments.CannotInflict"/>) and never_damaging_ailments stop it. faster_burn_% and damaging_ailments_deal_damage_+%_faster multiply the damage
        /// per second and divide the duration (wiki Ignite; PoB IgniteBurnFaster).</summary>
        public static Settings From(IEnumerable<(uint Stat, int Value)> rows, string hand, Func<uint, int> get) =>
            From(rows, hand, get, 0);

        public static Settings From(IEnumerable<(uint Stat, int Value)> rows, string hand, Func<uint, int> get,
            int additionalDurationPct)
        {
            if (MonsterElementalAilments.CannotInflict("ignite", get) || get(Stat.NeverDamagingAilments) != 0) return None;
            var r = rows.ToDictionary(x => x.Stat, x => x.Value);
            bool attackHand = hand is "main_hand" or "off_hand";
            double faster = Faster(get, attackHand);
            if (faster <= 0) return None;
            double Row(string bound, string kind) =>
                r.GetValueOrDefault(Stat.Of($"{bound}_ignite_damage_to_deal_per_minute_from_{kind}_{hand}_damage")) / 60.0 * faster;
            // Shaper of Flames: "Hits always Ignite" (always_ignite). Attack and weapon tincture ignite stats apply to weapon hands only.
            bool tinctureWeapon = attackHand && get(Stat.TinctureApplied) != 0;
            long chance = (long)get(Stat.BaseChanceToIgnitePct) + get(Stat.ChanceToFreezeShockIgnitePct)
                + (attackHand ? get(Stat.AttackIgniteChancePct) : 0)
                + (tinctureWeapon ? get(Stat.ChanceToIgnitePctWithTincturedWeapons) : 0)
                + (get(Stat.AlwaysIgnite) != 0 ? 100 : 0);
            double seconds = Ailments.IgniteDurationSeconds
                * Math.Max(0, 1 + ((double)get(Stat.IgniteDurationPlusPct) + get(Stat.BaseAllAilmentDurationPlusPct) + additionalDurationPct
                    + (tinctureWeapon ? get(Stat.ElementalAilmentDurationPlusPctWithTincturedWeapons) : 0)) / 100) / faster
                * DurationMultiplier(get);
            return new((int)Math.Clamp(chance, 0, 100), Row("minimum", "non_critical"), Row("maximum", "non_critical"),
                Row("minimum", "critical"), Row("maximum", "critical"), seconds,
                Stacks: (int)Math.Clamp(1L + get(Stat.NumberOfAdditionalIgnitesAllowed), 1, int.MaxValue),
                FireExposure: get(Stat.IgnitesApplyFireResistancePlus),
                AllDamage: get(Stat.AllDamageCanIgnite) != 0 || tinctureWeapon && get(Stat.AllDamageWithTincturedWeaponsCanIgnite) != 0,
                NeverFromCrit: get(Stat.KeystoneElementalOverload) != 0,
                OnlyFromCrit: get(Stat.KeystoneAilmentCrit) != 0)
            {
                FireCannotIgnite = get(Stat.UniqueFireDamageShocks) != 0,
                ColdCanIgnite = get(Stat.UniqueColdDamageIgnites) != 0,
                CriticalNeedsIgniteChance = get(Stat.CriticalStrikesDoNotAlwaysIgnite) != 0,
            };
        }
    }

    /// <summary>The "deal damage faster" factor of the attacker's ignites (1 without one).</summary>
    public static double Faster(Func<uint, int> get) => Faster(get, attack: false);

    static double Faster(Func<uint, int> get, bool attack) =>
        Math.Max(0, 1 + ((double)get(Stat.FasterBurnPct) + get(Stat.DamagingAilmentsDealDamagePlusPctFaster)
            + (attack ? get(Stat.FasterBurnFromAttacksPct) : 0)) / 100.0);

    /// <summary>The target's base_self_ignite_duration_-% (Fire and Ignite Resistant) on an ignite's duration.</summary>
    public static double SelfDuration(Func<uint, int> target) =>
        Math.Max(0, 1 - target(Stat.BaseSelfIgniteDurationMinusPct) / 100.0);

    /// <summary>Sadism's "Supported Skills have 70% less Duration" on their damaging ailments
    /// (support_faster_ailments_ailment_duration_+%_final). Its "deal damage faster" stat alone shortens the duration
    /// proportionally and raises the damage per second, so without this the ailment's total damage would not fall.</summary>
    public static double DurationMultiplier(Func<uint, int> get) =>
        Math.Max(0, 1 + get(Stat.SupportFasterAilmentsAilmentDurationPlusPctFinal) / 100.0);

    // Spread: its proliferation radius in cells (0: it does not spread). Proliferated: the copy another monster's
    // spreading ignite puts on this one, set again every AI tick (<see cref="Proliferated"/>).
    readonly record struct Ignite(double From, double Until, double DamagePerSecond, bool Aggravated = false, int Spread = 0,
        bool Proliferated = false, int DamageTakenPct = 0, ulong? Source = null, int ConcurrentLimit = 1);
    readonly List<Ignite> active = [];
    // The spreading ignites it had when it died: a corpse goes on spreading them until they end (wiki Ignite
    // Proliferation Support: the aura continues through death on intact corpses). They deal no damage.
    List<Ignite> corpse = [];
    double corpseIgnitedUntil = double.NegativeInfinity;

    /// <summary>When an ignite was last applied (Controlled Blaze's "Enemy Ignited Recently" window).</summary>
    public double LastApplied { get; private set; }

    /// <summary>How many of the strongest deal damage at once: 1, or Crimson Dance's 8 for bleeds.</summary>
    public int Stacks { get; set; } = 1;

    /// <summary>Aggravates every bleed on the monster for its remaining duration (wiki Bleeding).</summary>
    public void Aggravate()
    {
        for (int i = 0; i < active.Count; i++) active[i] = active[i] with { Aggravated = true };
    }

    /// <summary>Refreshes every live ignite's remaining duration to its full duration from <paramref name="now"/>
    /// (Wildfire Phloem). Returns true when at least one ignite was refreshed.</summary>
    public bool Refresh(double now)
    {
        bool refreshed = false;
        for (int i = 0; i < active.Count; i++)
        {
            var ig = active[i];
            if (ig.Until <= now) continue;
            double duration = Math.Max(0, ig.Until - ig.From);
            if (duration <= 0) continue;
            active[i] = ig with { From = now, Until = now + duration };
            refreshed = true;
        }
        return refreshed;
    }

    // The damage per second of the live instances: the Stacks strongest, plus aggravatedExtra × the strongest aggravated
    // one (an aggravated bleed's "as though moving" part, which does not stack with the moving bonus).
    double Dealing(List<Ignite> live, double aggravatedExtra)
    {
        int limit = Math.Max(1, Stacks);
        if (hasConcurrentIgnites)
            foreach (var ignite in live) limit = Math.Max(limit, ignite.ConcurrentLimit);
        return live.Select(i => i.DamagePerSecond).OrderDescending().Take(limit).Sum()
            + aggravatedExtra * live.Where(i => i.Aggravated).Select(i => i.DamagePerSecond).DefaultIfEmpty().Max();
    }

    bool hasConcurrentIgnites;
    double last, carry;
    int accrued;

    /// <summary>Settle damage before an effect changes, retaining it for the normal damage-commit tick.</summary>
    public void Accrue(double until, double factor, double aggravatedExtra = 0) =>
        accrued = Advance(until, factor, aggravatedExtra);

    double expiryRate = 1;
    public double ExpiryRate => expiryRate;

    public void SetExpiryRate(double now, double rate)
    {
        rate = Math.Max(.25, rate);
        for (int i = 0; i < active.Count; i++)
            active[i] = active[i] with { Until = RecipientEffectTime.RetimedDeadline(active[i].Until, now, expiryRate, rate) };
        expiryRate = rate;
    }
    public double RemainingBase(double now) => Math.Max(0, Until - now) * expiryRate;

    /// <summary>Discards uncommitted damage without removing or extending the timed instances.</summary>
    public void DiscardDamageCarry() { carry = 0; accrued = 0; }

    /// <summary>The native <c>ignited</c> buff instance on the monster while it burns (BuffDefinitions row 0).</summary>
    /// <summary>The row of the buff that shows the set (its source: BuffSource), null while none (Game.ShowDot).</summary>
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
    public bool BurningAt(double now) => active.Any(ignite => ignite.From <= now && now < ignite.Until);

    /// <summary>Nothing burns and no buff is shown.</summary>
    public bool Idle => active.Count == 0 && Buff is null && accrued == 0;

    /// <summary>When the last of its ignites ends.</summary>
    public double Until => active.Count == 0 ? 0 : active.Max(i => i.Until);

    /// <summary>The strongest damage-taken modifier retained by a currently live ignite.</summary>
    public int DamageTaken(double now)
    {
        int value = 0;
        foreach (var ignite in active)
            if (ignite.From <= now && now < ignite.Until) value = Math.Max(value, ignite.DamageTakenPct);
        return value;
    }

    /// <summary>Whether this monster or its intact corpse still has an unexpired ignite.</summary>
    public bool IgnitedAt(double now)
    {
        if (active.Count == 0) return now < corpseIgnitedUntil;
        foreach (var ignite in active)
            if (ignite.From <= now && now < ignite.Until) return true;
        return false;
    }

    /// <summary>The strongest ignite's damage per second at <paramref name="now"/> (0 when none burns; the
    /// <see cref="Stacks"/> strongest together).</summary>
    public double PerSecond(double now) => Dealing(active.Where(i => i.From <= now && now < i.Until).ToList(), 0);

    /// <summary>A hit that dealt fire damage tries to ignite. Critical hits always do; otherwise the chance rolls, then the
    /// target's avoidance. The damage per second is an independent roll in the context's range, as the incoming
    /// ailments roll theirs (<c>ApplyPlayerAilmentsLocked</c>). True when an ignite was added.</summary>
    public bool TryApply(Settings settings, bool critical, double now, int avoid, bool immune, Random random)
        => TryApply(settings, critical, now, avoid, immune, random, 0, out _);

    public bool TryApply(Settings settings, bool critical, double now, int avoid, bool immune, Random random,
        out double perSecond) => TryApply(settings, critical, now, avoid, immune, random, 0, out perSecond);

    public bool TryApply(Settings settings, bool critical, double now, int avoid, bool immune, Random random,
        int damageTakenPct) => TryApply(settings, critical, now, avoid, immune, random, damageTakenPct, out _);

    public bool TryApply(Settings settings, bool critical, double now, int avoid, bool immune, Random random,
        int damageTakenPct, out double perSecond)
    {
        perSecond = 0;
        if (settings.NeverFromCrit) critical = false;
        if (settings.OnlyFromCrit && !critical) return false;
        if (immune || settings.Seconds <= 0
            || (!critical || settings.CriticalNeedsIgniteChance) && !RandomRolls.Chance(settings.Chance, 100, random)
            || avoid > 0 && RandomRolls.Chance(avoid, 100, random)) return false;
        var (min, max) = critical ? (settings.CriticalMin, settings.CriticalMax) : (settings.Min, settings.Max);
        perSecond = min + (Math.Max(min, max) - min) * random.NextDouble();
        return Add(now, perSecond, settings.Seconds, settings.Aggravated, settings.Spread, damageTakenPct, settings.Stacks);
    }

    /// <summary>An ignite already worked out (its chance rolled, its damage and duration final). True when added.</summary>
    public bool Add(double now, double perSecond, double seconds, bool aggravated = false, int spread = 0) =>
        Add(now, perSecond, seconds, aggravated, spread, 0);

    static long spreadsAdded;
    /// <summary>True once any ignite with a proliferation radius has been added anywhere in the process.</summary>
    public static bool AnySpreadAdded => Volatile.Read(ref spreadsAdded) > 0;

    /// <summary>True when this monster has any active or corpse-retained ignite that might spread.</summary>
    public bool MaySpread => active.Count > 0 || corpse.Count > 0;

    public bool Add(double now, double perSecond, double seconds, bool aggravated, int spread, int damageTakenPct) =>
        Add(now, perSecond, seconds, aggravated, spread, damageTakenPct, 1);

    bool Add(double now, double perSecond, double seconds, bool aggravated, int spread, int damageTakenPct, int concurrentLimit)
    {
        if (!(perSecond > 0) || !(seconds > 0)) return false;
        if (active.Count == 0) { last = now; carry = 0; }
        int radius = Math.Max(0, spread);
        if (radius > 0) Interlocked.Increment(ref spreadsAdded);
        active.Add(new(now, now + seconds / expiryRate, perSecond, aggravated, radius,
            DamageTakenPct: Math.Max(0, damageTakenPct), ConcurrentLimit: concurrentLimit));
        hasConcurrentIgnites |= concurrentLimit > 1;
        LastApplied = now;
        return true;
    }

    /// <summary>Renews a strongest-only damage effect while its source still reaches this actor.</summary>
    public void Renew(double now, double perSecond, double seconds, ulong source)
    {
        if (!(perSecond > 0) || !(seconds > 0)) return;
        double end = now + seconds / expiryRate;
        int index = active.FindIndex(effect => effect.Source == source && effect.Until > now);
        if (index >= 0)
        {
            var current = active[index];
            if (current.DamagePerSecond == perSecond)
            {
                active[index] = current with { Until = end };
                return;
            }
            active[index] = current with { Until = now };
        }
        if (active.Count == 0) { last = now; carry = 0; }
        active.Add(new(now, end, perSecond, Source: source));
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
        int pending = accrued;
        accrued = 0;
        if (active.Count == 0 || !(until > last)) return pending;
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
        return (int)Math.Min(int.MaxValue, (long)pending + whole);
    }

    /// <summary>Ends a dead monster's ignites. <paramref name="burnt"/>: its ignite killed it, so it died ignited even if
    /// that ignite ran out in the same advance.</summary>
    public void Clear(bool burnt = false)
    {
        DiedBurning |= burnt || Burning;
        if (active.Count > 0)
        {
            corpseIgnitedUntil = Math.Max(corpseIgnitedUntil, active.Max(i => i.Until));
            corpse = active.Where(i => i.Spread > 0 && !i.Proliferated).ToList();
        }
        active.Clear();
        carry = 0;
        accrued = 0;
    }

    /// <summary>Drops a corpse's retained spreading ignites (shattered or consumed: nothing left to spread from).</summary>
    public void RetireCorpse()
    {
        corpse = [];
        corpseIgnitedUntil = double.NegativeInfinity;
    }

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
