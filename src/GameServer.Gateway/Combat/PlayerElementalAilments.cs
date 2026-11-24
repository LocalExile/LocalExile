using GameServer.Core.World;

/// <summary>Incoming ordinary monster-hit ailments. Connection-local, owned by the area's frame, on its pauseable clock.
/// Alternative ailments require an explicit source and are not substituted for the ordinary ailments here.</summary>
public sealed class PlayerElementalAilments
{
    // Target Metadata/Monsters/Monster.ot: critical_ailment_dot_multiplier_+ = 30.
    // Character.ot's +50 belongs to player ailments, not this incoming monster consumer.
    public const double MonsterCriticalAilmentMultiplier = 1.3;
    public enum Kind { Ignite, Chill, Freeze, Shock }
    /// <summary>The source skill's native ailment stats. Critical strikes and native chances share one application
    /// per kind, so the defender's avoidance is rolled once even when both would inflict the same ailment.</summary>
    public readonly record struct HitSettings(int IgniteChance, int FreezeChance, int ShockChance,
        int ChillEffectFinalPct, int ShockDurationFinalPct, int IgniteDamageFinalPct)
    {
        public bool NeverFreeze { get; init; }
        /// <summary>The source skill's own stats, for the rules that read more of them than the fields above.</summary>
        public Func<uint, int>? SourceStats { get; init; }
    }
    // Source: the monster whose hit inflicted it, for damage taken from that enemy (an ignite from a Blinded one).
    sealed record Effect(Kind Kind, double From, double Until, double Magnitude, uint? Source = null);
    readonly List<Effect> effects = [];
    readonly double[] protectedUntil = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
    double carry;
    double expiryRate = 1;
    // Lethe Shade: when an ignite was last applied, the onset of the player's damaging-ailment DoT (with the bleed and
    // poison starts in IncomingDamagingAilments). NegativeInfinity while none has landed.
    double latestIgniteStart = double.NegativeInfinity;
    public double LatestIgniteStart { get { lock (effects) return latestIgniteStart; } }
    public void SetExpiryRate(double now, double rate)
    {
        bool retimed;
        lock (effects)
        {
            rate = Math.Max(.25, rate);
            retimed = rate != expiryRate && effects.Count > 0;
            for (int i = 0; i < effects.Count; i++)
                effects[i] = effects[i] with { Until = RecipientEffectTime.RetimedDeadline(effects[i].Until, now, expiryRate, rate) };
            expiryRate = rate;
        }
        if (retimed) Changed?.Invoke();
    }
    double actionFloor, actionCeiling = double.PositiveInfinity, freezeEffect = 1;
    bool chillReversed, unaffectedByChill, unaffectedByFreeze, unaffectedByShock, unaffectedByIgnite;

    public (bool Ignite, int Chill, bool Freeze, int Shock) Snapshot(double now) =>
        (Magnitude(Kind.Ignite, now) > 0, (int)Magnitude(Kind.Chill, now), Magnitude(Kind.Freeze, now) > 0,
            (int)Magnitude(Kind.Shock, now));

    public bool HasEffects { get { lock (effects) return effects.Count > 0; } }

    /// <summary>An ailment was added, removed or retimed (the stats and presentation that show them follow).</summary>
    public Action? Changed;

    /// <summary>When an ailment next starts or ends after <paramref name="now"/>; null without one.</summary>
    public double? NextChangeAt(double now)
    {
        lock (effects)
        {
            double next = double.PositiveInfinity;
            foreach (var e in effects)
                next = Math.Min(next, e.From > now ? e.From : e.Until > now ? e.Until : double.PositiveInfinity);
            return double.IsFinite(next) ? next : null;
        }
    }

    public void Clear()
    {
        bool had;
        lock (effects)
        {
            had = effects.Count > 0;
            effects.Clear();
            Array.Fill(protectedUntil, double.NegativeInfinity);
            carry = 0;
            latestIgniteStart = double.NegativeInfinity;
        }
        if (had) Changed?.Invoke();
    }

    public bool IsProtected(Kind kind, double now)
    {
        lock (effects) return now < protectedUntil[(int)kind];
    }

    /// <summary>Conditional flask suffixes ("Grants Immunity to ... for X seconds if Used while ..."): remove the
    /// triggering ailment(s) when currently afflicted and block new applications for the rolled duration. Without an
    /// active triggering ailment, neither removes nor grants immunity.</summary>
    public bool RemoveOrProtectChillFreeze(double now, double seconds) =>
        RemoveOrProtect(now, seconds, Kind.Chill, Kind.Freeze);

    public bool RemoveOrProtectIgnite(double now, double seconds) =>
        RemoveOrProtect(now, seconds, Kind.Ignite);

    public bool RemoveOrProtectShock(double now, double seconds) =>
        RemoveOrProtect(now, seconds, Kind.Shock);

    bool RemoveOrProtect(double now, double seconds, params Kind[] kinds)
    {
        lock (effects)
        {
            effects.RemoveAll(e => e.Until <= now);
            bool active = effects.Any(e => Array.IndexOf(kinds, e.Kind) >= 0 && e.From <= now && now < e.Until);
            if (!active) return false;
            effects.RemoveAll(e => Array.IndexOf(kinds, e.Kind) >= 0);
            if (seconds > 0)
                foreach (var k in kinds)
                    protectedUntil[(int)k] = Math.Max(protectedUntil[(int)k], now + seconds);
        }
        Changed?.Invoke();
        return true;
    }
    public double Magnitude(Kind kind, double now)
    {
        lock (effects) return effects.Where(e => e.Kind == kind && e.From <= now && now < e.Until)
            .Select(e => e.Magnitude).DefaultIfEmpty().Max();
    }
    /// <summary>When the freeze on the player ends; <paramref name="now"/> without one.</summary>
    public double FreezeUntil(double now)
    {
        lock (effects) return unaffectedByFreeze ? now : FreezeStatusUntil(now);
    }

    // Unaffected suppresses its action hold, but the retained freeze still produces "been Frozen" history.
    public double FreezeStatusUntil(double now)
    {
        lock (effects) return effects.Where(e => e.Kind == Kind.Freeze && e.From <= now && now < e.Until)
            .Select(e => e.Until).DefaultIfEmpty(now).Max();
    }

    /// <summary>Action speed is one additive bucket: chill/freeze and the character's own action_speed_-% sum, then the
    /// result multiplies attack, cast and movement pacing. A positive <paramref name="statReduction"/> is slower; a
    /// negative value (Tailwind, Acceleration Shrine) is faster. Changing Seasons (chill_effect_is_reversed): the chill on
    /// you raises action speed by its effect instead of lowering it (PoB CalcPerform: ActionSpeed INC +effect); freeze
    /// still stops you (wiki Changing Seasons).</summary>
    public double TotalActionFactor(double now, double statReduction)
    {
        double freeze = unaffectedByFreeze ? 0 : Magnitude(Kind.Freeze, now) * freezeEffect,
            chill = unaffectedByChill ? 0 : Magnitude(Kind.Chill, now);
        double change = (chillReversed && freeze <= 0 ? chill : -Math.Max(freeze, chillReversed ? 0 : chill)) - statReduction;
        return Math.Min(actionCeiling, Math.Max(actionFloor, 1 + change / 100));
    }

    public double ActionFactor(double now) => TotalActionFactor(now, 0);
    // Unaffected by Shock (PoB SelfShockEffect 100% less): the shock stays on the player and in is_shocked, with no effect.
    // increasedPct: the hit's other increased damage taken (from a Blinded enemy), additive with the shock's.
    public double DamageTakenFactor(double now, int increasedPct = 0) =>
        Math.Max(0, 1 + ((unaffectedByShock ? 0 : Magnitude(Kind.Shock, now)) + increasedPct) / 100);

    static string Name(Kind kind) => kind.ToString().ToLowerInvariant();
    // Per Kind: immune_to_*, cannot_be_*, self_*_duration_-% and avoid_*_%.
    static readonly (uint Immune, uint CannotBe, uint SelfDuration, uint Avoid)[] KindStats = [.. Enum.GetValues<Kind>().Select(kind =>
        (Stat.Of($"immune_to_{Name(kind)}"),
            Stat.Of($"cannot_be_{kind switch { Kind.Ignite => "ignited", Kind.Chill => "chilled", Kind.Freeze => "frozen", _ => "shocked" }}"),
            Stat.Of($"self_{Name(kind)}_duration_-%"), Stat.Of($"avoid_{Name(kind)}_%")))];

    static bool Immune(Kind kind, Func<uint, int> get) => get(KindStats[(int)kind].Immune) != 0
        || get(KindStats[(int)kind].CannotBe) != 0;

    public void Reconcile(Func<uint, int> get)
    {
        lock (effects)
        {
            if (effects.RemoveAll(e => Immune(e.Kind, get)) > 0) Changed?.Invoke();
            freezeEffect = Math.Max(0, 1 + get(Stat.BaseFrozenEffectOnSelfPlusPct) / 100.0);
            unaffectedByChill = get(Stat.UnaffectedByChill) != 0;
            unaffectedByFreeze = get(Stat.UnaffectedByFreeze) != 0;
            unaffectedByShock = get(Stat.UnaffectedByShock) != 0;
            unaffectedByIgnite = get(Stat.UnaffectedByIgnite) != 0;
            actionFloor = get(Stat.ActionSpeedCannotBeReducedBelowBase) != 0
                || get(Stat.NotAffectedByActionSpeedModifiers) != 0 ? 1 : 0;
            actionCeiling = get(Stat.NotAffectedByActionSpeedModifiers) != 0 ? 1 : double.PositiveInfinity;
            chillReversed = get(Stat.ChillEffectIsReversed) != 0;
        }
    }

    // Damaging ailments use the pre-critical base, not damage taken. Non-damaging ailments use damage dealt
    // against the ordinary player's maximum-Life threshold. No enemy chance-to-ailment modifiers are invented.
    public void Apply(DamagePacket baseHit, DamagePacket taken, bool critical, uint maxLife, double now,
        Func<uint, int> get, Random random, uint? source = null, double durationFactor = 1) =>
        Apply(baseHit, taken, critical, maxLife, now, get, random, default, source, durationFactor);

    public void Apply(DamagePacket baseHit, DamagePacket taken, bool critical, uint maxLife, double now,
        Func<uint, int> get, Random random, HitSettings settings, uint? source = null, double durationFactor = 1)
    {
        lock (effects)
        {
            Reconcile(get);
            // The caller's duration factor scales the ailments a hit puts on you; a reflected one (Inflict) is excluded.
            void Add(Kind kind, double magnitude, double seconds) => AddLocked(kind, magnitude,
                seconds * durationFactor, now, get, random, source);
            if (critical || RandomRolls.Chance(settings.IgniteChance, 100, random))
                Add(Kind.Ignite, baseHit[DamageType.Fire].Min * .9 * (critical ? MonsterCriticalAilmentMultiplier : 1)
                    * Math.Max(0, 1 + settings.IgniteDamageFinalPct / 100.0), 4);
            if (maxLife == 0) return;
            double cold = taken[DamageType.Cold].Min / maxLife;
            double lightning = taken[DamageType.Lightning].Min / maxLife;
            // Changing Seasons (all_damage_taken_can_chill): every type of the hit's damage counts toward its chill.
            double chilling = get(Stat.AllDamageTakenCanChill) != 0 ? taken.MinTotal / maxLife : cold;
            double chill = Math.Min(30, Math.Floor(50 * Math.Pow(chilling, .4)
                * Math.Max(0, 1 + EffectOnSelfPct(Kind.Chill, get) / 100.0)
                * Math.Max(0, 1 + settings.ChillEffectFinalPct / 100.0)));
            if (chill >= 5) Add(Kind.Chill, chill, 2);
            if (!settings.NeverFreeze && (critical || RandomRolls.Chance(settings.FreezeChance, 100, random)))
                Add(Kind.Freeze, 100, Math.Min(3, 6 * cold));
            double shock = Math.Min(50, Math.Floor(50 * Math.Pow(lightning, .4)
                * Math.Max(0, 1 + EffectOnSelfPct(Kind.Shock, get) / 100.0)));
            if (shock >= 5 && (critical || RandomRolls.Chance(settings.ShockChance, 100, random)))
                Add(Kind.Shock, shock, 2 * Math.Max(0, 1 + settings.ShockDurationFinalPct / 100.0));
        }
    }

    // The increased effect of a chill or shock on the player: its own stat plus the non-damaging ailments' one
    // (chill and shock are non-damaging ailments, ReminderTextNonDamagingAilments), which the two add to.
    static int EffectOnSelfPct(Kind kind, Func<uint, int> get) =>
        get(kind == Kind.Chill ? Stat.ChillEffectivenessOnSelfPlusPct : Stat.ShockedEffectOnSelfPlusPct)
        + get(Stat.NonDamagingAilmentEffectPlusPctOnSelf);

    void AddLocked(Kind kind, double magnitude, double seconds, double now, Func<uint, int> get, Random random,
        uint? source = null)
    {
        if (now < protectedUntil[(int)kind]) return;
        seconds *= Math.Max(0, 1 - get(KindStats[(int)kind].SelfDuration) / 100.0);
        uint avoid = KindStats[(int)kind].Avoid;
        if (magnitude <= 0 || seconds <= 0 || kind == Kind.Freeze && seconds < .3 || Immune(kind, get)
            || RandomRolls.Chance(get(avoid), 100, random)) return;
        effects.Add(new(kind, now, now + seconds / expiryRate, magnitude, source));
        if (kind == Kind.Ignite) latestIgniteStart = now;
        Changed?.Invoke();
    }

    // A chill, freeze or shock reflected back onto the player (a Mirrored monster) with the effect and duration it
    // landed with: then the player's own effect and duration modifiers, caps, immunity and avoidance, as for a hit.
    public void Inflict(Kind kind, double magnitude, double seconds, double now, Func<uint, int> get, Random random)
    {
        lock (effects)
        {
            Reconcile(get);
            if (kind is Kind.Chill or Kind.Shock)
                magnitude = Math.Min(kind == Kind.Chill ? 30 : 50,
                    Math.Floor(magnitude * Math.Max(0, 1 + EffectOnSelfPct(kind, get) / 100.0)));
            if (kind is not (Kind.Chill or Kind.Shock) || magnitude >= 5) AddLocked(kind, magnitude, seconds, now, get, random);
        }
    }

    // Integrate strongest ignite through every start/end boundary: a weaker, longer ignite can resume.
    // Resistance/immunity are live; hit luck, block, suppression and the 90% hit override never enter this pass.
    // Unaffected by Ignite (PoB SelfIgniteEffect 100% less): the ignite stays on the player and in is_ignited, dealing nothing.
    // takenFrom: the increased damage taken from the strongest ignite's source monster (a Blinded one), with the shock's.
    public int Advance(double from, double until, Func<uint, int> get, int fireResistance, int maxFireResistance,
        bool fireImmune, Func<uint, int>? takenFrom = null, double dotTakenFactor = 1)
    {
        lock (effects)
        {
            Reconcile(get);
            if (until <= from) return 0;
            var boundaries = effects.SelectMany(e => new[] { e.From, e.Until }).Where(t => from < t && t < until)
                .Append(from).Append(until).Distinct().Order().ToArray();
            double damage = carry;
            for (int i = 1; i < boundaries.Length; i++)
            {
                double at = (boundaries[i - 1] + boundaries[i]) / 2;
                if (fireImmune || unaffectedByIgnite || effects.Where(e => e.Kind == Kind.Ignite && e.From <= at && at < e.Until)
                        .MaxBy(e => e.Magnitude) is not { } ignite) continue;
                int fromSource = ignite.Source is uint source && takenFrom is not null ? takenFrom(source) : 0;
                damage += DamageModel.Mitigate(ignite.Magnitude, fireResistance, maxFireResistance)
                    * DamageTakenFactor(at, fromSource) * (boundaries[i] - boundaries[i - 1]);
            }
            effects.RemoveAll(e => e.Until <= until);
            damage *= dotTakenFactor;
            int whole = (int)Math.Min(int.MaxValue, Math.Floor(damage));
            carry = damage - whole;
            return whole;
        }
    }
}
