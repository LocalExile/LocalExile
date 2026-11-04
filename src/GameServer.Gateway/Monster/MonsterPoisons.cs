using GameServer.Core.World;

/// <summary>
/// The player's poisons on one monster. Multiple poisons can stack indefinitely; each deals chaos damage concurrently
/// and maintains its own duration (default 2 s). Guarded by the connection's Monsters lock.
/// </summary>
sealed class MonsterPoisons
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
    public readonly record struct Settings(int Chance, double Min, double Max, double CriticalMin, double CriticalMax,
        double Seconds, int WeaponChance = 0, int MoreChance = 0)
    {
        public static readonly Settings None = new(0, 0, 0, 0, 0, 0);

        /// <summary><paramref name="local"/>: the attacking weapon's local stats (none for a spell).
        /// <paramref name="flaskEffect"/>: Master Toxicist's extra-damage chance applies only "during any Flask
        /// Effect".</summary>
        public static Settings From(IEnumerable<(string Stat, int Value)> rows, string hand, Func<string, int> get,
            IReadOnlyDictionary<string, int>? local = null, bool flaskEffect = false)
        {
            if (Prohibited(get)) return None;
            var r = rows.ToDictionary(x => x.Stat, x => x.Value);
            double faster = Faster(get);
            if (faster <= 0) return None;
            double Row(string bound, string kind) =>
                r.GetValueOrDefault($"{bound}_poison_damage_to_deal_per_minute_from_{kind}_{hand}_damage") / 60.0 * faster;
            long chance = (long)get("base_chance_to_poison_on_hit_%") + get("chaos_damage_chance_to_poison_%");
            double seconds = Ailments.PoisonDurationSeconds
                * Math.Max(0, 1 + ((double)get("poison_duration_+%") + get("base_all_ailment_duration_+%")
                    + get("base_poison_duration_+%")) / 100) / faster;
            int moreChance = flaskEffect
                ? Math.Clamp(get("pathfinder_poison_damage_+100%_final_chance_during_flask_effect"), 0, 100) : 0;
            return new((int)Math.Clamp(chance, 0, 100), Row("minimum", "non_critical"), Row("maximum", "non_critical"),
                Row("minimum", "critical"), Row("maximum", "critical"), seconds,
                local?.GetValueOrDefault("local_poison_on_hit_%") ?? 0, moreChance);
        }
    }

    /// <summary>Whether the attacker cannot poison: poison deals chaos damage, so a chaos prohibition stops it even from
    /// physical damage (PoB CalcOffence gates poison on <c>canDeal.Chaos</c>).</summary>
    public static bool Prohibited(Func<string, int> get) =>
        get("cannot_poison_enemies") != 0 || get("never_damaging_ailments") != 0
        || DamageStats.Prohibited(DamageType.Chaos, get);

    public static double Faster(Func<string, int> get) =>
        Math.Max(0, 1 + ((double)get("faster_poison_%") + get("damaging_ailments_deal_damage_+%_faster")) / 100.0);

    public static double SelfDuration(Func<string, int> target) =>
        Math.Max(0, 1 - target("base_self_poison_duration_-%") / 100.0);

    readonly record struct Poison(double From, double Until, double DamagePerSecond);
    readonly List<Poison> active = [];
    double last, carry;

    /// <summary>The native <c>poison</c> buff instance on the monster (BuffDefinitions row 10).</summary>
    public ushort? Buff;

    public bool Poisoned => active.Count > 0;

    /// <summary>Whether a stack is running at <paramref name="now"/> (the poison clock), for "a Poisoned Enemy" at death.</summary>
    public bool PoisonedAt(double now) => active.Any(p => p.From <= now && now < p.Until);

    public bool Idle => active.Count == 0 && Buff is null;

    public double Until => active.Count == 0 ? 0 : active.Max(p => p.Until);

    public int Stacks(double now) => active.Count(p => p.From <= now && now < p.Until);

    public double TotalDps(double now) =>
        active.Where(p => p.From <= now && now < p.Until).Sum(p => p.DamagePerSecond);

    /// <summary>The latest poison this monster received, for Master Toxicist's on-kill spread (the stack is gone once the
    /// body is cleared, its receipt is not).</summary>
    public readonly record struct Receipt(double DamagePerSecond, double Seconds);
    public Receipt? LastApplied { get; private set; }

    public bool TryApply(Settings settings, bool critical, double now, int avoid, bool immune, Random random)
    {
        if (immune || settings.Seconds <= 0 || !RandomRolls.Chance(settings.Chance, 100, random)
            || avoid > 0 && RandomRolls.Chance(avoid, 100, random)) return false;
        var (min, max) = critical ? (settings.CriticalMin, settings.CriticalMax) : (settings.Min, settings.Max);
        double dps = min + (Math.Max(min, max) - min) * random.NextDouble();
        if (settings.MoreChance > 0 && RandomRolls.Chance(settings.MoreChance, 100, random)) dps *= 2;
        return Add(now, dps, settings.Seconds);
    }

    public bool Add(double now, double perSecond, double seconds)
    {
        if (!(perSecond > 0) || !(seconds > 0)) return false;
        if (active.Count == 0) { last = now; carry = 0; }
        LastApplied = new(perSecond, seconds);
        active.Add(new(now, now + seconds / expiryRate, perSecond));
        return true;
    }

    /// <summary>Sums the concurrent chaos damage of all active poison stacks from last advance to <paramref name="until"/>,
    /// multiplied by the monster's chaos mitigation <paramref name="factor"/>.</summary>
    public int Advance(double until, double factor)
    {
        if (active.Count == 0 || !(until > last)) return 0;
        var cuts = active.SelectMany(p => new[] { p.From, p.Until }).Where(t => last < t && t < until)
            .Append(last).Append(until).Distinct().Order().ToArray();
        double damage = carry;
        for (int k = 1; k < cuts.Length; k++)
        {
            double mid = (cuts[k - 1] + cuts[k]) / 2;
            double sumDps = active.Where(p => p.From <= mid && mid < p.Until).Sum(p => p.DamagePerSecond);
            damage += sumDps * Math.Max(0, factor) * (cuts[k] - cuts[k - 1]);
        }
        last = until;
        active.RemoveAll(p => p.Until <= until);
        int whole = (int)Math.Min(int.MaxValue, Math.Floor(Math.Max(0, damage)));
        carry = active.Count == 0 ? 0 : damage - whole;
        return whole;
    }

    public void Clear() { DiedPoisoned |= Poisoned; active.Clear(); carry = 0; }

    public bool DiedPoisoned { get; private set; }
}
