using GameServer.Core.World;

/// <summary>
/// The player's poisons on one monster. Multiple poisons can stack indefinitely; each deals chaos damage concurrently
/// and maintains its own duration (default 2 s). Owned by the area's frame.
/// </summary>
public sealed class MonsterPoisons
{
    double expiryRate = 1;
    public void SetExpiryRate(double now, double rate)
    {
        rate = Math.Max(.25, rate);
        for (int i = 0; i < active.Count; i++)
            active[i] = active[i] with { Until = RecipientEffectTime.RetimedDeadline(active[i].Until, now, expiryRate, rate) };
        expiryRate = rate;
    }
    /// <param name="WeaponChance">The attacking weapon's own local_poison_on_hit_% ("X% chance to Poison on Hit" on
    /// it), which the panel's chance counts for that hand's attacks.</param>
    /// <param name="ProjectileChance">The qualified Yriel projectile-attack poison chance frozen at release. It is
    /// additive to the contact's live generic attack chance and is not read again at contact.</param>
    public readonly record struct Settings(int Chance, double Min, double Max, double CriticalMin, double CriticalMax,
        double Seconds, int WeaponChance = 0, int ProjectileChance = 0, bool Secondary = false,
        // Elemental Overload: "Ailments never count as being from Critical Strikes".
        bool NeverFromCrit = false,
        // Perfect Agony: "Non-Critical Strikes cannot inflict Ailments".
        bool OnlyFromCrit = false)
    {
        public static readonly Settings None = new(0, 0, 0, 0, 0, 0);

        public bool AllDamageWithWeapon { get; init; }
        public int AdditionalWithWeapon { get; init; }

        /// <summary><paramref name="local"/>: the attacking weapon's local stats (none for a spell).</summary>
        public static Settings From(IEnumerable<(uint Stat, int Value)> rows, string hand, Func<uint, int> get,
            IReadOnlyDictionary<uint, int>? local = null, bool projectileAttack = false)
        {
            if (Prohibited(get)) return None;
            var r = rows.ToDictionary(x => x.Stat, x => x.Value);
            double faster = Faster(get);
            if (faster <= 0) return None;
            double Row(string bound, string kind) =>
                r.GetValueOrDefault(Stat.Of($"{bound}_poison_damage_to_deal_per_minute_from_{kind}_{hand}_damage")) / 60.0 * faster;
            long chance = (long)get(Stat.BaseChanceToPoisonOnHitPct) + get(Stat.ChaosDamageChanceToPoisonPct);
            int projectileChance = projectileAttack
                ? Math.Clamp(get(Stat.ProjectileAttacksChanceToPoisonOnHitPctIfYouHaveBeastMinion), 0, 100) : 0;
            double seconds = BaseSeconds(get) / faster * MonsterIgnites.DurationMultiplier(get);
            return new((int)Math.Clamp(chance, 0, 100), Row("minimum", "non_critical"), Row("maximum", "non_critical"),
                Row("minimum", "critical"), Row("maximum", "critical"), seconds,
                local?.GetValueOrDefault(Stat.LocalPoisonOnHitPct) ?? 0, projectileChance, Secondary: hand == "secondary",
                NeverFromCrit: get(Stat.KeystoneElementalOverload) != 0,
                OnlyFromCrit: get(Stat.KeystoneAilmentCrit) != 0)
            {
                AllDamageWithWeapon = local?.GetValueOrDefault(Stat.LocalAllDamageCanPoison) is not (null or 0),
                AdditionalWithWeapon = local?.GetValueOrDefault(Stat.LocalNumAdditionalPoisonsToInflictWhenInflictingPoison) ?? 0,
            };
        }
    }

    /// <summary>Whether the attacker cannot poison: poison deals chaos damage, so a chaos prohibition stops it even from
    /// physical damage (PoB CalcOffence gates poison on <c>canDeal.Chaos</c>).</summary>
    public static bool Prohibited(Func<uint, int> get) =>
        get(Stat.Of("cannot_poison_enemies")) != 0 || get(Stat.NeverDamagingAilments) != 0
        || DamageStats.Prohibited(DamageType.Chaos, get);

    public static double BaseSeconds(Func<uint, int> get) => Ailments.PoisonDurationSeconds
        * Math.Max(0, 1 + ((double)get(Stat.BasePoisonDurationPlusPct) + get(Stat.PoisonDurationPlusPct)
            + get(Stat.BaseAllAilmentDurationPlusPct)) / 100);

    public static double Faster(Func<uint, int> get) =>
        Math.Max(0, 1 + ((double)get(Stat.FasterPoisonPct) + get(Stat.DamagingAilmentsDealDamagePlusPctFaster)) / 100.0);

    public static double SelfDuration(Func<uint, int> target) =>
        Math.Max(0, 1 - target(Stat.Of("base_self_poison_duration_-%")) / 100.0);

    readonly record struct Poison(double From, double Until, double DamagePerSecond);
    readonly List<Poison> active = [];
    double last, carry;

    /// <summary>The native <c>poison</c> buff instance on the monster (BuffDefinitions row 10).</summary>
    /// <summary>The row of the buff that shows the set (its source: BuffSource), null while none (Game.ShowDot).</summary>
    public ushort? Buff;

    /// <summary>The attacking object shown as the buff source (0 for generic self-created poisons).</summary>
    public uint BuffSource;

    /// <summary>Moves the active stacks and integration state to a fresh owner without restarting either.</summary>
    public void MoveTo(MonsterPoisons target)
    {
        if (ReferenceEquals(this, target)) return;
        target.active.AddRange(active);
        target.last = last;
        target.carry = carry;
        target.expiryRate = expiryRate;
        target.DiedPoisoned = DiedPoisoned;
        target.BuffSource = BuffSource;
        target.Buff = null;

        active.Clear();
        last = 0;
        carry = 0;
        expiryRate = 1;
        DiedPoisoned = false;
        BuffSource = 0;
        Buff = null;
    }

    public bool Poisoned => active.Count > 0;

    public bool Idle => active.Count == 0 && Buff is null;

    public double Until => active.Count == 0 ? 0 : active.Max(p => p.Until);

    public int Stacks(double now) => active.Count(p => p.From <= now && now < p.Until);

    public double TotalDps(double now) =>
        active.Where(p => p.From <= now && now < p.Until).Sum(p => p.DamagePerSecond);

    public bool TryApply(Settings settings, bool critical, double now, int avoid, bool immune, Random random)
    {
        if (settings.NeverFromCrit) critical = false;
        if (settings.OnlyFromCrit && !critical) return false;
        if (immune || settings.Seconds <= 0 || !RandomRolls.Chance(settings.Chance, 100, random)
            || avoid > 0 && RandomRolls.Chance(avoid, 100, random)) return false;
        var (min, max) = critical ? (settings.CriticalMin, settings.CriticalMax) : (settings.Min, settings.Max);
        double dps = min + (Math.Max(min, max) - min) * random.NextDouble();
        return Add(now, dps, settings.Seconds);
    }

    public bool Add(double now, double perSecond, double seconds)
    {
        if (!(perSecond > 0) || !(seconds > 0)) return false;
        cleared = advancedDeath = null; DiedPoisoned = false;
        if (active.Count == 0) { last = now; carry = 0; }
        active.Add(new(now, now + seconds / expiryRate, perSecond));
        return true;
    }

    /// <summary>Sums the concurrent chaos damage of all active poison stacks from last advance to <paramref name="until"/>,
    /// multiplied by the monster's chaos mitigation <paramref name="factor"/>.</summary>
    public int Advance(double until, double factor, uint? lifeAtStart = null) => Advance(until, factor, out _, lifeAtStart);

    /// <summary>The same advance, also reporting the end of the last cut where the active stacks dealt positive damage:
    /// not the frame time, and null when no positive mitigated subinterval existed.</summary>
    public int Advance(double until, double factor, out double? lastDamageAt, uint? lifeAtStart = null)
    {
        lastDamageAt = null;
        if (active.Count == 0 || !(until > last)) return 0;
        advancedDeath = null;
        var cuts = active.SelectMany(p => new[] { p.From, p.Until }).Where(t => last < t && t < until)
            .Append(last).Append(until).Distinct().Order().ToArray();
        double damage = carry;
        for (int k = 1; k < cuts.Length; k++)
        {
            double mid = (cuts[k - 1] + cuts[k]) / 2;
            double sumDps = active.Where(p => p.From <= mid && mid < p.Until).Sum(p => p.DamagePerSecond);
            double seconds = cuts[k] - cuts[k - 1];
            double mitigated = sumDps * Math.Max(0, factor);
            if (mitigated > 0 && seconds > 0) lastDamageAt = cuts[k];

            double stepDamage = sumDps * Math.Max(0, factor) * (cuts[k] - cuts[k - 1]);
            // Preserve the stacks at the lethal contact, even when this advance also expires them. Death callbacks
            // run after the step and cannot recover that state from the live list.
            if (advancedDeath is null && lifeAtStart is > 0 && mitigated > 0 && damage + stepDamage >= lifeAtStart.Value)
            {
                double at = cuts[k - 1] + Math.Clamp((lifeAtStart.Value - damage) / mitigated, 0, seconds);
                advancedDeath = CaptureDeath(at, mid);
            }
            damage += stepDamage;
        }
        last = until;
        active.RemoveAll(p => p.Until <= until);
        int whole = (int)Math.Min(int.MaxValue, Math.Floor(Math.Max(0, damage)));
        carry = active.Count == 0 ? 0 : damage - whole;
        return whole;
    }

    public void Clear()
    {
        active.Clear(); carry = 0;
        cleared = advancedDeath = null; DiedPoisoned = false;
    }

    public void ClearAtDeath(double now, bool poisonDeath = false)
    {
        cleared = (poisonDeath ? advancedDeath : null) ?? cleared
            ?? CaptureDeath(now, now);
        DiedPoisoned = cleared is not null; active.Clear(); carry = 0;
    }

    /// <summary>Freeze the poison on an accepted death before asynchronous kill presentation can outlive it.</summary>
    public void RememberDeath(double now)
    {
        cleared = CaptureDeath(now, now);
        DiedPoisoned = cleared is not null;
    }

    readonly record struct DeathPoison(double At, double Remaining, (double DamagePerSecond, double Until) Strongest);
    DeathPoison? cleared, advancedDeath;

    /// <summary>The death's timestamp and remaining poison damage before target mitigation. A deferred death callback
    /// retains that snapshot even after the poison ticker drops the live stacks.</summary>
    public (double At, double Remaining)? RemainingAtDeath(double now)
    {
        var death = cleared ?? CaptureDeath(now, now);
        return death is { } value ? (value.At, value.Remaining) : null;
    }

    DeathPoison? CaptureDeath(double at, double sample)
    {
        (double DamagePerSecond, double Until)? strongest = null;
        double remaining = 0;
        foreach (var poison in active)
        {
            if (poison.From <= at && at < poison.Until)
                remaining += poison.DamagePerSecond * (poison.Until - at) * expiryRate;
            if (poison.From <= sample && sample < poison.Until
                && (strongest is not { } old || poison.DamagePerSecond > old.DamagePerSecond
                    || poison.DamagePerSecond == old.DamagePerSecond && poison.Until > old.Until))
                strongest = (poison.DamagePerSecond, poison.Until);
        }
        return strongest is { } best ? new(at, remaining, best) : null;
    }

    /// <summary>The highest damage stack still running at <paramref name="now"/>, or the one the death that cleared the
    /// poisons ended (<see cref="ClearAtDeath"/>): a deferred kill callback keeps the state of that death.</summary>
    public (double DamagePerSecond, double Until)? Strongest(double now) =>
        cleared?.Strongest ?? StrongestOf(active.Where(p => p.From <= now && now < p.Until));

    static (double DamagePerSecond, double Until)? StrongestOf(IEnumerable<Poison> stacks)
    {
        (double, double)? best = null;
        foreach (var p in stacks)
            if (best is not { } b || p.DamagePerSecond > b.Item1 || p.DamagePerSecond == b.Item1 && p.Until > b.Item2)
                best = (p.DamagePerSecond, p.Until);
        return best;
    }

    public bool DiedPoisoned { get; private set; }
}
