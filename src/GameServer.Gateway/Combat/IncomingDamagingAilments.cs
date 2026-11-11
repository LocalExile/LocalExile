using GameServer.Core.World;

/// <summary>
/// The damaging ailments other objects have put on the player. Every accepted bleed is retained so a weaker
/// bleed can continue after a stronger one expires; only the strongest active bleed deals damage. Poison stacks
/// expire independently. Resource damage is advanced only by the player recovery owner under the Monsters lock.
/// </summary>
sealed class IncomingDamagingAilments
{
    internal readonly record struct State(bool Bleeding, double BleedUntil, int Poisons, double PoisonUntil,
        long BleedRevision, long PoisonRevision);

    readonly record struct Bleed(double From, double Until, double DamagePerSecond, uint? Source, bool Reflected);
    readonly record struct Poison(double From, double Until, double DamagePerSecond, uint? Source, bool Reflected);

    readonly List<Bleed> bleeds = [];
    readonly List<Poison> poisons = [];
    double bleedFraction, poisonFraction, bleedTakenFraction, poisonTakenFraction;
    double bleedProtectedUntil = double.NegativeInfinity, poisonProtectedUntil = double.NegativeInfinity;
    double last = double.NegativeInfinity;
    long bleedRevision, poisonRevision;

    public bool Bleeding(double now) => bleeds.Any(b => Active(b.From, b.Until, now));
    public int PoisonStacks(double now) => poisons.Count(p => Active(p.From, p.Until, now));
    public double BleedPerSecond(double now) =>
        bleeds.Where(b => Active(b.From, b.Until, now)).Select(b => b.DamagePerSecond).DefaultIfEmpty(0).Max();
    public double PoisonPerSecond(double now) =>
        poisons.Where(p => Active(p.From, p.Until, now)).Sum(p => p.DamagePerSecond);
    public double BleedingUntil(double now) =>
        bleeds.Where(b => Active(b.From, b.Until, now)).Select(b => b.Until).DefaultIfEmpty(double.NegativeInfinity).Max();
    public double PoisonUntil(double now) =>
        poisons.Where(p => Active(p.From, p.Until, now)).Select(p => p.Until)
            .DefaultIfEmpty(double.NegativeInfinity).Max();
    public bool Idle => bleeds.Count == 0 && poisons.Count == 0;

    internal State Snapshot(double now) =>
        new(Bleeding(now), BleedingUntil(now), PoisonStacks(now), PoisonUntil(now), bleedRevision, poisonRevision);

    /// <summary>
    /// Adds an accepted bleed. Weak and equal instances are retained: they are candidates once stronger
    /// instances expire. <paramref name="source"/> names the accepted contact. <paramref name="reflected"/> is
    /// provenance only: removing the source that admitted it does not remove an instance already on the victim.
    /// </summary>
    public bool ApplyBleed(double now, double perSecond, double seconds, uint? source = null, bool reflected = false,
        Func<string, int>? victim = null)
    {
        if (now < bleedProtectedUntil) return false;
        if (!(perSecond > 0) || !(seconds > 0)) return false;
        seconds *= SelfDuration(victim, bleed: true);
        if (!(seconds > 0)) return false;
        bleeds.Add(new(now, now + seconds, perSecond, source, reflected));
        bleedRevision++;
        return true;
    }

    /// <summary>Adds one independently expiring poison stack.</summary>
    public bool ApplyPoison(double now, double perSecond, double seconds, uint? source = null, bool reflected = false,
        Func<string, int>? victim = null)
    {
        if (now < poisonProtectedUntil) return false;
        if (!(perSecond > 0) || !(seconds > 0)) return false;
        seconds *= SelfDuration(victim, bleed: false);
        if (!(seconds > 0)) return false;
        poisons.Add(new(now, now + seconds, perSecond, source, reflected));
        poisonRevision++;
        return true;
    }

    /// <summary>
    /// Removes states rejected by current immunity without changing the order of accepted instances. Reflection
    /// grants do not own admitted ailments: losing the jewel stops future reflection, not an existing bleed or poison.
    /// Returns whether any instance was removed.
    /// </summary>
    public bool Reconcile(Func<string, int> get)
    {
        int before = bleeds.Count + poisons.Count;
        if (PreventsBleeding(get))
        {
            RemoveBleeds(_ => true);
            bleedTakenFraction = 0;
        }
        if (PreventsPoison(get))
        {
            RemovePoisons(_ => true);
            poisonTakenFraction = 0;
        }
        return bleeds.Count + poisons.Count != before;
    }

    /// <summary>
    /// A paid recovery flask's conditional bleed suffix: when used while bleeding, remove the accepted bleed and
    /// protect against new bleeding for its extracted duration. With no current bleed the suffix does nothing.
    /// </summary>
    public bool RemoveOrProtectBleeding(double now, double seconds)
    {
        Prune(now);
        if (!Bleeding(now)) return false;
        RemoveBleeds(_ => true);
        if (seconds > 0) bleedProtectedUntil = Math.Max(bleedProtectedUntil, now + seconds);
        return true;
    }

    /// <summary>The poison counterpart of <see cref="RemoveOrProtectBleeding"/>.</summary>
    public bool RemoveOrProtectPoison(double now, double seconds)
    {
        Prune(now);
        if (PoisonStacks(now) == 0) return false;
        RemovePoisons(_ => true);
        if (seconds > 0) poisonProtectedUntil = Math.Max(poisonProtectedUntil, now + seconds);
        return true;
    }

    /// <summary>
    /// Advances both ailments over the exact unused game-time interval. Every instance start and expiry
    /// partitions the interval; the strongest bleed active on each partition contributes, while every active
    /// poison contributes. Expiry and carry are settled even when the victim is immune or unaffected. No Ward
    /// is involved: bleeding uses physical ES routing and poison is Chaos damage.
    /// </summary>
    public int Advance(double from, double until, CharacterStats.Pools pools, EnergyShield shield,
        Func<string, int> get, int bleedResistance, int bleedMaxResistance, int poisonResistance,
        int poisonMaxResistance, bool immune, bool physicalImmune, bool chaosImmune,
        Func<double, double, double> movingSeconds,
        ref double nonChaosBypassCarry, ref double chaosBypassCarry)
    {
        Reconcile(get);
        from = Math.Max(from, last);
        Prune(from);
        if (!(until > from))
        {
            if (until > last) last = until;
            return 0;
        }

        if (!immune)
        {
            // The wall-to-wall partitions make mid-interval applications and expiries explicit.
            var cuts = new List<double> { from, until };
            foreach (var b in bleeds)
            {
                if (b.From > from && b.From < until) cuts.Add(b.From);
                if (b.Until > from && b.Until < until) cuts.Add(b.Until);
            }
            foreach (var p in poisons)
            {
                if (p.From > from && p.From < until) cuts.Add(p.From);
                if (p.Until > from && p.Until < until) cuts.Add(p.Until);
            }
            cuts.Sort();
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                double start = cuts[i], end = cuts[i + 1];
                if (!(end > start)) continue;
                double seconds = end - start;
                double strongest = bleeds.Where(x => Active(x.From, x.Until, start))
                    .Select(x => x.DamagePerSecond).DefaultIfEmpty(0).Max();
                if (strongest > 0)
                {
                    double moved = get("no_extra_bleed_damage_while_moving") == 0
                        ? Math.Clamp(movingSeconds(start, end), 0, seconds) : 0;
                    bleedFraction += strongest * (seconds + 2 * moved);
                }
                foreach (var p in poisons)
                    if (Active(p.From, p.Until, start)) poisonFraction += p.DamagePerSecond * seconds;
            }
        }
        else
        {
            // God/grace immunity neither damages nor leaves a fraction to charge the next state owner.
            bleedFraction = poisonFraction = bleedTakenFraction = poisonTakenFraction = 0;
        }

        bool unaffectedBleed = get("virtual_unaffected_by_bleeding") != 0
            || get("base_unaffected_by_bleeding") != 0;
        bool unaffectedPoison = get("virtual_unaffected_by_poison") != 0
            || get("base_unaffected_by_poison") != 0;
        // Resistance and typed damage taken keep their fractions across update partitions. Round only after the
        // whole interval has reached this owner; the resource writer receives whole points as it always has.
        double bleedMitigated = unaffectedBleed || physicalImmune ? 0
            : DamageModel.Mitigate(bleedFraction, bleedResistance, bleedMaxResistance);
        double poisonMitigated = unaffectedPoison || chaosImmune ? 0
            : DamageModel.Mitigate(poisonFraction, poisonResistance, poisonMaxResistance);
        bleedFraction = poisonFraction = 0;
        if (unaffectedBleed || physicalImmune)
        {
            bleedTakenFraction = 0;
        }
        if (unaffectedPoison || chaosImmune)
        {
            poisonTakenFraction = 0;
        }

        int less = IncomingResourceDefences.LessTakenPct(pools, get);
        int bleedRaw = IncomingResourceDefences.TakenOverTime(bleedMitigated, get, ref bleedTakenFraction, less,
            typedIncreasedPct: get("physical_damage_taken_+%"),
            reductionPct: ChargeEffects.Reduction(get).Physical,
            finalTakenPct: get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        int poisonRaw = IncomingResourceDefences.TakenOverTime(poisonMitigated, get, ref poisonTakenFraction, less,
            typedIncreasedPct: get("chaos_damage_taken_+%"),
            finalTakenPct: get(IncomingResourceDefences.UnhingedSaneDamageFinal));

        Prune(until);
        last = until;
        if (bleeds.Count == 0) bleedFraction = 0;
        if (poisons.Count == 0) poisonFraction = 0;

        int life = 0;
        if (bleedRaw > 0)
            life += IncomingResourceDefences.DamageOverTime(shield, bleedRaw,
                get(IncomingResourceDefences.BypassStat), until, ref nonChaosBypassCarry);
        if (poisonRaw > 0)
            life += IncomingResourceDefences.DamageOverTime(shield, poisonRaw,
                get("chaos_damage_does_not_bypass_energy_shield") != 0 ? 0 : 100,
                until, ref chaosBypassCarry);
        return life;
    }

    /// <summary>Clears all stacks and fractional carry for death, resurrection and real area teardown.</summary>
    public void Clear()
    {
        RemoveBleeds(_ => true);
        RemovePoisons(_ => true);
        bleedFraction = poisonFraction = bleedTakenFraction = poisonTakenFraction = 0;
        bleedProtectedUntil = poisonProtectedUntil = double.NegativeInfinity;
        last = double.NegativeInfinity;
        // Keep revisions monotonic so a presentation snapshot from before a lifecycle clear can never compare
        // equal to a fresh empty state.
        bleedRevision++;
        poisonRevision++;
    }

    const double TimeEpsilon = 1e-9;
    static bool Active(double from, double until, double now) =>
        from <= now + TimeEpsilon && now + TimeEpsilon < until;

    void Prune(double at)
    {
        bleeds.RemoveAll(b => b.Until <= at);
        poisons.RemoveAll(p => p.Until <= at);
        if (bleeds.Count == 0) bleedFraction = bleedTakenFraction = 0;
        if (poisons.Count == 0) poisonFraction = poisonTakenFraction = 0;
    }

    void RemoveBleeds(Predicate<Bleed> match)
    {
        int removed = bleeds.RemoveAll(match);
        if (removed > 0) bleedRevision++;
        if (bleeds.Count == 0) bleedFraction = bleedTakenFraction = 0;
    }

    void RemovePoisons(Predicate<Poison> match)
    {
        int removed = poisons.RemoveAll(match);
        if (removed > 0) poisonRevision++;
        if (poisons.Count == 0) poisonFraction = poisonTakenFraction = 0;
    }

    static bool PreventsBleeding(Func<string, int> get) =>
        get("cannot_gain_bleeding") != 0 || get("base_cannot_gain_bleeding") != 0
        || get("virtual_immune_to_bleeding") != 0 || get("immune_to_bleeding") != 0;

    static bool PreventsPoison(Func<string, int> get) =>
        get("cannot_be_poisoned") != 0 || get("immune_to_poison") != 0;

    static double SelfDuration(Func<string, int>? get, bool bleed)
    {
        if (get is null) return 1;
        string combined = bleed ? "combined_bleeding_duration_on_self_+%" : "combined_poison_duration_on_self_+%";
        string plain = bleed ? "self_bleed_duration_+%" : "self_poison_duration_+%";
        string final = bleed ? "self_bleed_duration_+%_final" : "self_poison_duration_+%_final";
        string combinedFinal = bleed ? "combined_self_bleed_duration_+%_final" : "combined_self_poison_duration_+%_final";
        double increased = get(combined);
        if (increased == 0) increased = get(plain);
        return Math.Max(0, 1 + increased / 100.0)
            * Math.Max(0, 1 + get(final) / 100.0)
            * Math.Max(0, 1 + get(combinedFinal) / 100.0)
            * Math.Max(0, 1 + get(bleed ? "pantheon_shakari_self_bleed_duration_+%_final"
                : "pantheon_shakari_self_poison_duration_+%_final") / 100.0);
    }

}
