using GameServer.Core.World;

/// <summary>Incoming ordinary monster-hit ailments. Connection-local, guarded by Monsters, on its pauseable clock.
/// Alternative ailments require an explicit source and are not substituted for the ordinary ailments here.</summary>
public sealed class PlayerElementalAilments
{
    // Target Metadata/Monsters/Monster.ot: critical_ailment_dot_multiplier_+ = 30.
    // Character.ot's +50 belongs to player ailments, not this incoming monster consumer.
    public const double MonsterCriticalAilmentMultiplier = 1.3;
    public enum Kind { Ignite, Chill, Freeze, Shock }
    public readonly record struct RecipientModifiers(int AvoidancePct, double DurationFactor);
    /// <summary>The source skill's native ailment stats. Critical strikes and native chances share one application
    /// per kind, so the defender's avoidance is rolled once even when both would inflict the same ailment.</summary>
    public readonly record struct HitSettings(int IgniteChance, int FreezeChance, int ShockChance,
        int ChillEffectFinalPct, int ShockDurationFinalPct, int IgniteDamageFinalPct)
    {
        public bool NeverFreeze { get; init; }
        /// <summary>The source skill's own stats, for the rules that read more of them than the fields above.</summary>
        public Func<string, int>? SourceStats { get; init; }
    }
    // Source: the monster whose hit inflicted it, for damage taken from that enemy (an ignite from a Blinded one).
    sealed record Effect(Kind Kind, double From, double Until, double Magnitude, uint? Source = null);
    readonly List<Effect> effects = [];
    double carry;
    double expiryRate = 1;
    // Lethe Shade: when an ignite was last applied, the onset of the player's damaging-ailment DoT (with the bleed and
    // poison starts in IncomingDamagingAilments). NegativeInfinity while none has landed.
    double latestIgniteStart = double.NegativeInfinity;
    public double LatestIgniteStart { get { lock (effects) return latestIgniteStart; } }
    public void SetExpiryRate(double now, double rate)
    {
        lock (effects)
        {
            rate = Math.Max(.25, rate);
            for (int i = 0; i < effects.Count; i++)
                effects[i] = effects[i] with { Until = RecipientEffectTime.RetimedDeadline(effects[i].Until, now, expiryRate, rate) };
            expiryRate = rate;
        }
    }
    double actionFloor, actionCeiling = double.PositiveInfinity, freezeEffect = 1;
    bool chillReversed, unaffectedByChill, unaffectedByFreeze, unaffectedByShock, unaffectedByIgnite;

    public (bool Ignite, int Chill, bool Freeze, int Shock) Snapshot(double now) =>
        (Magnitude(Kind.Ignite, now) > 0, (int)Magnitude(Kind.Chill, now), Magnitude(Kind.Freeze, now) > 0,
            (int)Magnitude(Kind.Shock, now));

    public bool HasEffects { get { lock (effects) return effects.Count > 0; } }

    public void Clear() { lock (effects) { effects.Clear(); carry = 0; latestIgniteStart = double.NegativeInfinity; } }
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
    static bool Immune(Kind kind, Func<string, int> get) => get($"immune_to_{Name(kind)}") != 0
        || get($"cannot_be_{kind switch { Kind.Ignite => "ignited", Kind.Chill => "chilled", Kind.Freeze => "frozen", _ => "shocked" }}") != 0;

    public void Reconcile(Func<string, int> get)
    {
        lock (effects)
        {
            effects.RemoveAll(e => Immune(e.Kind, get));
            freezeEffect = Math.Max(0, 1 + get("base_frozen_effect_on_self_+%") / 100.0);
            unaffectedByChill = get("unaffected_by_chill") != 0;
            unaffectedByFreeze = get("unaffected_by_freeze") != 0;
            unaffectedByShock = get("unaffected_by_shock") != 0;
            unaffectedByIgnite = get("unaffected_by_ignite") != 0;
            actionFloor = get("action_speed_cannot_be_reduced_below_base") != 0
                || get("not_affected_by_action_speed_modifiers") != 0 ? 1 : 0;
            actionCeiling = get("not_affected_by_action_speed_modifiers") != 0 ? 1 : double.PositiveInfinity;
            chillReversed = get("chill_effect_is_reversed") != 0;
        }
    }

    // Damaging ailments use the pre-critical base, not damage taken. Non-damaging ailments use damage dealt
    // against the ordinary player's maximum-Life threshold. No enemy chance-to-ailment modifiers are invented.
    public void Apply(DamagePacket baseHit, DamagePacket taken, bool critical, uint maxLife, double now,
        Func<string, int> get, Random random, uint? source = null, double durationFactor = 1) =>
        Apply(baseHit, taken, critical, maxLife, now, get, random, default, source, durationFactor);

    public void Apply(DamagePacket baseHit, DamagePacket taken, bool critical, uint maxLife, double now,
        Func<string, int> get, Random random, HitSettings settings, uint? source = null, double durationFactor = 1,
        Func<string, RecipientModifiers>? recipient = null)
    {
        lock (effects)
        {
            Reconcile(get);
            // The caller's duration factor scales the ailments a hit puts on you; a reflected one (Inflict) is excluded.
            void Add(Kind kind, double magnitude, double seconds) => AddLocked(kind, magnitude,
                seconds * durationFactor, now, get, random, source, recipient);
            if (critical || RandomRolls.Chance(settings.IgniteChance, 100, random))
                Add(Kind.Ignite, baseHit[DamageType.Fire].Min * .9 * (critical ? MonsterCriticalAilmentMultiplier : 1)
                    * Math.Max(0, 1 + settings.IgniteDamageFinalPct / 100.0), 4);
            if (maxLife == 0) return;
            double cold = taken[DamageType.Cold].Min / maxLife;
            double lightning = taken[DamageType.Lightning].Min / maxLife;
            // Changing Seasons (all_damage_taken_can_chill): every type of the hit's damage counts toward its chill.
            double chilling = get("all_damage_taken_can_chill") != 0 ? taken.MinTotal / maxLife : cold;
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
    static int EffectOnSelfPct(Kind kind, Func<string, int> get) =>
        get(kind == Kind.Chill ? "chill_effectiveness_on_self_+%" : "shocked_effect_on_self_+%")
        + get("non_damaging_ailment_effect_+%_on_self");

    void AddLocked(Kind kind, double magnitude, double seconds, double now, Func<string, int> get, Random random,
        uint? source = null, Func<string, RecipientModifiers>? recipient = null)
    {
        if (magnitude <= 0 || seconds <= 0 || Immune(kind, get)) return;
        string name = Name(kind);
        var mods = recipient?.Invoke(name) ?? new(get($"avoid_{name}_%"), Math.Max(0, 1 - get($"self_{name}_duration_-%") / 100.0));
        seconds *= mods.DurationFactor;
        if (seconds <= 0 || kind == Kind.Freeze && seconds < .3
            || RandomRolls.Chance(mods.AvoidancePct, 100, random)) return;
        effects.Add(new(kind, now, now + seconds / expiryRate, magnitude, source));
        if (kind == Kind.Ignite) latestIgniteStart = now;
    }

    // A chill, freeze or shock reflected back onto the player (a Mirrored monster) with the effect and duration it
    // landed with: then the player's own effect and duration modifiers, caps, immunity and avoidance, as for a hit.
    public void Inflict(Kind kind, double magnitude, double seconds, double now, Func<string, int> get, Random random,
        Func<string, RecipientModifiers>? recipient = null)
    {
        lock (effects)
        {
            Reconcile(get);
            if (kind is Kind.Chill or Kind.Shock)
                magnitude = Math.Min(kind == Kind.Chill ? 30 : 50,
                    Math.Floor(magnitude * Math.Max(0, 1 + EffectOnSelfPct(kind, get) / 100.0)));
            if (kind is not (Kind.Chill or Kind.Shock) || magnitude >= 5)
                AddLocked(kind, magnitude, seconds, now, get, random, recipient: recipient);
        }
    }

    // Integrate strongest ignite through every start/end boundary: a weaker, longer ignite can resume.
    // Resistance/immunity are live; hit luck, block, suppression and the 90% hit override never enter this pass.
    // Unaffected by Ignite (PoB SelfIgniteEffect 100% less): the ignite stays on the player and in is_ignited, dealing nothing.
    // takenFrom: the increased damage taken from the strongest ignite's source monster (a Blinded one), with the shock's.
    public int Advance(double from, double until, Func<string, int> get, int fireResistance, int maxFireResistance,
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
