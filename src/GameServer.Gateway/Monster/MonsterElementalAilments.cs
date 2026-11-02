using GameServer.Core.World;

/// <summary>
/// The chill, freeze and shock the player's hits put on one monster (wiki Chill, Freeze and Shock; PoB CalcOffence's
/// non-damaging ailments). Each inflicted ailment keeps its own magnitude and duration; only the strongest of a kind
/// applies. They stay on a corpse until they run out. Guarded by the Monsters lock, on the connection's pauseable clock.
/// </summary>
sealed class MonsterElementalAilments
{
    internal enum Kind { Chill, Freeze, Shock }
    internal sealed record Effect(Kind Kind, double From, double Until, double Magnitude)
    {
        // Glacial Wave: a chill from a hit of a player with enemies_chilled_by_hits_take_damage_increased_by_chill_effect
        // / ..._deal_damage_lessened_by_chill_effect, as the player had them when it landed.
        public bool MoreTaken { get; init; }
        public bool LessDealt { get; init; }
        // Its proliferation radius in cells when it landed; 0 for one that does not spread, as a spread copy.
        public int Spread { get; init; }
        // Within the Tempest's shock on the corpse, which came after the death (a spread copy of it did not).
        public bool OnKill { get; init; }
    }
    readonly List<Effect> effects = [];
    // The strongest of each kind another enemy's proliferation puts on this one, set again on every AI tick.
    readonly Dictionary<Kind, Effect> spread = [];
    // What the client was last sent: each kind's magnitude, the action speed stat and the factor its walk speed carries.
    readonly Dictionary<Kind, int> shown = [];
    public int SentActionSpeed;
    public double WalkFactor = 1;

    public bool Idle => effects.Count == 0 && spread.Count == 0 && shown.Count == 0 && SentActionSpeed == 0 && WalkFactor == 1;

    IEnumerable<Effect> Active(Kind kind, double now) => (spread.Count == 0 ? effects : effects.Concat(spread.Values))
        .Where(e => e.Kind == kind && e.From <= now && now < e.Until);

    // The AI asks every living monster each tick, and most have no ailment: that case allocates nothing.
    public double Magnitude(Kind kind, double now) =>
        effects.Count == 0 && spread.Count == 0 ? 0 : Active(kind, now).Select(e => e.Magnitude).DefaultIfEmpty().Max();

    /// <summary>Whether it was shocked when it died: Within the Tempest's shock on its corpse does not count.</summary>
    public bool ShockedAtDeath(double now) => Active(Kind.Shock, now).Any(e => !e.OnKill && e.Magnitude > 0);

    /// <summary>Seconds until the last active effect of <paramref name="kind"/> ends (0 when none).</summary>
    public double Remaining(Kind kind, double now) => Active(kind, now).Select(e => e.Until - now).DefaultIfEmpty().Max();

    /// <summary>Action speed: 0 while frozen, else less by the chill's effect.</summary>
    public double ActionFactor(double now) => Magnitude(Kind.Freeze, now) > 0 ? 0 : 1 - Magnitude(Kind.Chill, now) / 100;

    /// <summary>Glacial Wave: while a chill from such a hit lasts, the monster takes increased damage by its strongest
    /// chill's effect (from any source, wiki Glacial Wave; PoB CalcPerform DamageTaken INC).</summary>
    public double ChillDamageTakenPct(double now) => Active(Kind.Chill, now).Any(e => e.MoreTaken) ? Magnitude(Kind.Chill, now) : 0;

    /// <summary>Glacial Wave: and deals less damage by half that effect (PoB CalcPerform Damage MORE −effect / 2).</summary>
    public double ChillDamageDealtFactor(double now) =>
        Active(Kind.Chill, now).Any(e => e.LessDealt) ? 1 - Magnitude(Kind.Chill, now) / 200 : 1;

    /// <summary>
    /// virtual_{kind}_proliferation_radius as the client's calculators build it, the largest of its sources. Shock
    /// (0x142828340): the gloves', Elemental Proliferation's (virtual_elemental_status_effect_aura_radius, itself the larger
    /// of its two sources), Vinktar's flask's, the ascendancy's and the mastery's radius, and 15 with
    /// shock_proliferation_radius_is_at_least_15 (Within the Tempest). Freeze (0x142828810): Elemental Proliferation's, 12
    /// with spread_freeze_to_nearby_enemies, the gloves', the ascendancy's and the amulet's. Chill (0x142828c00): Elemental
    /// Proliferation's and the ascendancy's. The calculators scale none of them by area of effect; <see cref="SpreadRadius"/>
    /// does.
    /// </summary>
    internal static int ProliferationRadius(Kind kind, Func<string, int> get)
    {
        int elemental = Math.Max(get("elemental_status_effect_aura_radius"), get("elementalist_elemental_status_effect_aura_radius"));
        int ascendancy = get("ascendancy_non_damaging_elemental_ailment_proliferation_radius");
        int radius = kind switch
        {
            Kind.Chill => Math.Max(elemental, ascendancy),
            Kind.Freeze => new[]
            {
                elemental, get("spread_freeze_to_nearby_enemies") != 0 ? 12 : 0, get("gloves_freeze_proliferation_radius"),
                ascendancy, get("amulet_freeze_proliferation_radius"),
            }.Max(),
            _ => new[]
            {
                get("gloves_shock_proliferation_radius"), elemental, get("unique_vinktars_flask_shock_proliferation_radius"),
                ascendancy, get("passive_mastery_shock_proliferation_radius"),
                get("shock_proliferation_radius_is_at_least_15") != 0 ? 15 : 0,
            }.Max(),
        };
        return Math.Max(0, radius);
    }

    /// <summary>The cells an ailment spreads: that radius grown by the area of effect modifier <paramref name="area"/>
    /// (SkillArea.Modifier) like a skill's area, × its square root truncated to two places (wiki Elemental proliferation:
    /// the area of proliferation is affected by modifiers to area of effect).</summary>
    internal static int SpreadRadius(Kind kind, Func<string, int> attacker, double area) =>
        (int)Math.Floor(ProliferationRadius(kind, attacker) * Math.Floor(100 * Math.Sqrt(Math.Max(0, area))) / 100);

    /// <summary>This monster's own chills, freezes and shocks that spread, alive or a corpse. Each is an aura of its own
    /// radius (wiki Elemental proliferation), so a weaker one with a wider radius still reaches the enemies beyond a
    /// stronger one's.</summary>
    public IEnumerable<Effect> Spreading(double now) => effects.Count == 0 ? [] : effects
        .Where(e => e.Spread > 0 && e.From <= now && now < e.Until);

    /// <summary>Proliferation (wiki Elemental proliferation): the strongest spreading ailment of a kind whose radius
    /// reaches this monster affects it at the same effect until that one ends, like an aura; the copy does not spread
    /// again and is not a new ailment. Null, or a monster immune to the kind, takes it off.</summary>
    public void Proliferated(Kind kind, Effect? source, Func<string, int> target)
    {
        if (source is null || Immune(kind, target)) spread.Remove(kind);
        else spread[kind] = source with { Spread = 0, OnKill = false };
    }

    /// <summary>Chill and shock effect: floor(50 × (damage / threshold)^0.4 × effect modifiers), up to the cap, and none
    /// below 5% (the modifiers count before both limits). A <paramref name="minimum"/> ("Shocks from your Hits always
    /// increase Damage taken by at least X%") raises the base before the modifiers (wiki Shock), so a hit that deals the
    /// ailment's damage gets at least it.</summary>
    internal static int Effectiveness(double damage, double threshold, double modifiers, int cap, int minimum = 0)
    {
        if (damage <= 0 || threshold <= 0) return 0;
        int effect = (int)Math.Min(cap, Math.Floor(Math.Max(50 * Math.Pow(damage / threshold, .4), minimum) * modifiers));
        return effect >= 5 ? effect : 0;
    }

    static string Name(Kind kind) => kind.ToString().ToLowerInvariant();

    static bool Immune(Kind kind, Func<string, int> get)
    {
        string past = kind switch { Kind.Chill => "chilled", Kind.Freeze => "frozen", _ => "shocked" };
        return get($"immune_to_{Name(kind)}") != 0 || get($"cannot_be_{past}") != 0 || get($"base_cannot_be_{past}") != 0;
    }

    /// <summary>
    /// One hit's ailments from the damage the monster took per type (<paramref name="damage"/>, critical multiplier
    /// included) against its ailment threshold. Chill: every hit with chilling damage, 2 s. Freeze: a critical strike or
    /// chance_to_freeze_% with freezing damage, 0.06 s per 1% of the threshold up to 3 s, then duration modifiers,
    /// discarded under 0.3 s. Shock: a critical strike, chance_to_shock_% or always_shock ("Hits always Shock") with
    /// shocking damage, 2 s. Which types chill, freeze or shock is <see cref="Inflicting"/>; the effect reads their total
    /// (wiki Shock). <paramref name="attacker"/> gives the player's and the skill's
    /// summed stats, <paramref name="target"/> the monster's own; the attacker's prohibitions (<see cref="CannotInflict"/>) stop
    /// each ailment. The damage the chill, freeze and shock read takes the attacker's as-though multipliers (Overcharge).
    /// The effects added are returned. A chill carries the
    /// attacker's Glacial Wave stats, and each its proliferation radius grown by the hit's area modifier <paramref name="area"/>.
    /// </summary>
    public List<Effect> Inflict(DamagePacket damage, bool critical, double threshold, double now,
        Func<string, int> attacker, Func<string, int> target, Random random, double area = 1)
    {
        var added = new List<Effect>();
        if (threshold <= 0) return added;
        void Add(Effect effect) { if (Apply(effect, attacker, target) is { } e) added.Add(e); }
        double chilling = Inflicting("chill", DamageType.Cold, damage, attacker);
        double freezing = Inflicting("freeze", DamageType.Cold, damage, attacker);
        double shocking = Inflicting("shock", DamageType.Lightning, damage, attacker);
        if (!CannotInflict("chill", attacker))
            Add(new(Kind.Chill, now, now + 2, Effectiveness(chilling * AsThough(Kind.Chill, attacker), threshold,
                EffectModifier(Kind.Chill, attacker, target), ChillCap(attacker), attacker("chill_minimum_slow_%")))
            {
                MoreTaken = attacker("enemies_chilled_by_hits_take_damage_increased_by_chill_effect") != 0,
                LessDealt = attacker("enemies_chilled_by_hits_deal_damage_lessened_by_chill_effect") != 0,
                Spread = SpreadRadius(Kind.Chill, attacker, area),
            });
        // The chance rolls first, so a prohibition leaves the random sequence of the hits after it unchanged.
        if (freezing > 0 && (critical || RandomRolls.Chance(attacker("chance_to_freeze_%"), 100, random))
            && !CannotInflict("freeze", attacker))
            Add(new(Kind.Freeze, now, now + Math.Min(3, 6 * freezing * AsThough(Kind.Freeze, attacker) / threshold), 100)
                { Spread = SpreadRadius(Kind.Freeze, attacker, area) });
        if (shocking > 0 && (critical || attacker("always_shock") != 0
                || RandomRolls.Chance(attacker("chance_to_shock_%"), 100, random))
            && !CannotInflict("shock", attacker))
            Add(new(Kind.Shock, now, now + 2, Effectiveness(shocking * AsThough(Kind.Shock, attacker), threshold,
                EffectModifier(Kind.Shock, attacker, target), ShockCap(attacker), attacker("shock_minimum_damage_taken_increase_%")))
                { Spread = SpreadRadius(Kind.Shock, attacker, area) });
        return added;
    }

    /// <summary>The damage that counts for an ailment: its own element's, and another type's with "All Damage with Hits can
    /// Shock" (all_damage_can_*), "Your Chaos Damage can Shock" (one type's, Stormy Seas' cold among them) or, for shock,
    /// "Your Elemental Damage can Shock". Each type counts once. "Your Cold Damage cannot Freeze" (Replica Tasalio's
    /// Sign) keeps a type out whatever lets it, as a cannot does.</summary>
    static double Inflicting(string ailment, DamageType own, DamagePacket damage, Func<string, int> attacker)
    {
        bool Counts(DamageType type)
        {
            string name = type.ToString().ToLowerInvariant();
            return attacker($"{name}_damage_cannot_{ailment}") == 0 && (type == own
                || attacker($"all_damage_can_{ailment}") != 0 || attacker($"{name}_damage_can_{ailment}") != 0
                || ailment == "shock" && DamageTypes.IsElemental(type) && attacker("elemental_damage_can_shock") != 0);
        }
        return damage.Components.Where(c => Counts(c.Type)).Sum(c => c.Range.Min);
    }

    /// <summary>Whether the attacker's hits cannot inflict this elemental ailment ("ignite", "chill", "freeze" or
    /// "shock"): Elemental Focus's cannot_inflict_status_ailments ("Supported Skills cannot inflict Elemental Ailments"),
    /// never_ignite_chill_freeze_shock, and the per-ailment never_* stats; never_freeze_or_chill covers both cold
    /// ailments.</summary>
    public static bool CannotInflict(string ailment, Func<string, int> attacker) =>
        attacker("cannot_inflict_status_ailments") != 0 || attacker("never_ignite_chill_freeze_shock") != 0
        || attacker($"never_{ailment}") != 0
        || ailment is "chill" or "freeze" && attacker("never_freeze_or_chill") != 0;

    /// <summary>Brine Bound (chance_to_freeze_enemies_for_1_second_when_hit_%): a freeze of a set base duration, through
    /// the same duration modifiers, threshold, minimum and immunity as a hit's, spreading by the player's area modifier
    /// <paramref name="area"/>.</summary>
    public Effect? Freeze(double seconds, double now, Func<string, int> attacker, Func<string, int> target, double area = 1) =>
        Apply(new(Kind.Freeze, now, now + seconds, 100) { Spread = SpreadRadius(Kind.Freeze, attacker, area) }, attacker, target);

    /// <summary>Shatter: the monster left no corpse, so its ailments end with it (the next presentation takes their buffs
    /// off) and none spreads.</summary>
    public void Shatter()
    {
        effects.Clear();
        spread.Clear();
        Shattered = true;
    }

    /// <summary>A chill or shock that deals no damage ("Chill Attackers for 4 seconds on Block"), for <paramref name="seconds"/>
    /// through a hit's duration modifiers and immunity, from the <paramref name="stated"/> effect when its source gives one
    /// ("reducing their Action Speed by 30%"). It spreads by the player's area modifier <paramref name="area"/>.</summary>
    public Effect? WithoutDamage(Kind kind, double seconds, double now, Func<string, int> attacker, Func<string, int> target,
        double area = 1, int? stated = null) =>
        Apply(new(kind, now, now + seconds, BaseWithoutDamage(kind, attacker, target, stated))
            { Spread = SpreadRadius(kind, attacker, area) }, attacker, target);

    // Chill and shock without damage: 10% and 15% base unless the source states its own (wiki Chill and Shock), through
    // the effect modifiers, up to the cap.
    static double BaseWithoutDamage(Kind kind, Func<string, int> attacker, Func<string, int> target, int? stated = null) =>
        Math.Min(kind == Kind.Chill ? ChillCap(attacker) : ShockCap(attacker),
            Math.Floor((stated ?? (kind == Kind.Chill ? 10 : 15)) * EffectModifier(kind, attacker, target)));

    /// <summary>A hit killed it while frozen and it shattered: its freeze is gone, but the kill was of a frozen enemy.</summary>
    public bool Shattered { get; private set; }

    /// <summary>Within the Tempest (enemy_shock_on_kill): the shock a killed monster's corpse gets, 15% base for 2 s
    /// through the shock effect and duration modifiers (wiki Within the Tempest; the 15% of shocks without damage, wiki
    /// Shock), up to the shock cap. It spreads like any shock the player inflicts, by the player's area modifier <paramref name="area"/>.</summary>
    public Effect? ShockOnKill(double now, Func<string, int> attacker, Func<string, int> target, double area = 1) =>
        Apply(new(Kind.Shock, now, now + 2, BaseWithoutDamage(Kind.Shock, attacker, target))
            { Spread = SpreadRadius(Kind.Shock, attacker, area), OnKill = true }, attacker, target);

    // "Shock/Chill/Freeze Enemies as though dealing X% more Damage" (PoB CalcOffence: the ailment's damage times each
    // <Ailment>AsThoughDealing more): the damage the magnitude or the freeze duration reads, not the hit's, nor the chance.
    // Weapon-local, per-hand and skill-named ones (Manabond, Stormbind, graft skills) are not read.
    static readonly Dictionary<Kind, string[]> AsThoughStats = new()
    {
        [Kind.Chill] = ["active_skill_chill_as_though_damage_+%_final", "unique_quiver_chill_as_though_damage_+%_final",
            "non_damaging_ailments_as_though_damage_+%_final"],
        [Kind.Freeze] = ["freeze_as_though_dealt_damage_+%", "helmet_mod_freeze_as_though_damage_+%_final",
            "ice_nova_freeze_as_though_damage_+%_final", "non_damaging_ailments_as_though_damage_+%_final"],
        [Kind.Shock] = ["support_pure_shock_shock_as_though_damage_+%_final", "active_skill_shock_as_though_damage_+%_final",
            "helmet_mod_shock_as_though_damage_+%_final", "non_damaging_ailments_as_though_damage_+%_final"],
    };

    static double AsThough(Kind kind, Func<string, int> attacker) =>
        AsThoughStats[kind].Aggregate(1.0, (more, name) => more * Math.Max(0, 1 + attacker(name) / 100.0));

    // The caps on the effect, 30% chill and 50% shock: Shaper of Winter's override ("Your Chills can reduce Action Speed by
    // up to a maximum of 40%") and Voltaxic Rift's +40%, whose stat is stored as 100, the legacy override's value (wiki
    // Voltaxic Rift). "+X% to Maximum Effect of Shock" is not read: only removed content (runesmithing, Royale) has it.
    static int ChillCap(Func<string, int> attacker) =>
        attacker("elementalist_chill_maximum_magnitude_override") is > 0 and int cap ? cap : 30;

    static int ShockCap(Func<string, int> attacker) =>
        50 + (attacker("unique_voltaxic_rift_shock_maximum_magnitude_override") != 0 ? 40 : 0);

    static double EffectModifier(Kind kind, Func<string, int> attacker, Func<string, int> target) =>
        Math.Max(0, 1 + attacker($"virtual_{Name(kind)}_effect_+%") / 100.0)
        * Math.Max(0, 1 + target(kind == Kind.Chill ? "chill_effectiveness_on_self_+%" : "shocked_effect_on_self_+%") / 100.0);

    /// <summary>One inflicted ailment with its base duration: the attacker's and target's duration modifiers, a freeze
    /// under 0.3 s discarded, the target's immunity. Null when it does not apply. The attacker's increase and the
    /// target's increase on itself add up (PoB CalcOffence: Enemy…Duration INC + Self…Duration INC); a positive
    /// base_self_*_duration_-% is a reduction.</summary>
    Effect? Apply(Effect effect, Func<string, int> attacker, Func<string, int> target)
    {
        var (kind, now) = (effect.Kind, effect.From);
        double seconds = (effect.Until - now) * Math.Max(0, 1 + (attacker($"virtual_{Name(kind)}_duration_+%")
            - target($"base_self_{Name(kind)}_duration_-%")) / 100.0);
        if (effect.Magnitude <= 0 || seconds <= 0 || kind == Kind.Freeze && seconds < .3 || Immune(kind, target)) return null;
        // Brine Bound (freeze_minimum_duration_X_ms): a freeze that passes the threshold lasts at least this long, which no
        // duration modifier changes (wiki Brine Bound).
        if (kind == Kind.Freeze) seconds = Math.Max(seconds, attacker("freeze_minimum_duration_X_ms") / 1000.0);
        effect = effect with { Until = now + seconds };
        // A weaker one of the same sort that also ends sooner can never be the strongest again.
        effects.RemoveAll(e => e.Until <= now || e.Kind == kind && e.Magnitude <= effect.Magnitude && e.Until <= effect.Until
            && (e.MoreTaken, e.LessDealt, e.Spread) == (effect.MoreTaken, effect.LessDealt, effect.Spread));
        effects.Add(effect);
        return effect;
    }

    /// <summary>The buff instance each kind always has on its monster: the top of the range, which
    /// <see cref="ClientState.NextBuff"/> never hands out, so a monster's frequent ailment changes take nothing from the
    /// connection's counter and never meet another buff's instance on the same object.</summary>
    internal static ushort Instance(Kind kind) => (ushort)(FirstInstance + (int)kind);
    internal const ushort FirstInstance = ushort.MaxValue - 2;

    /// <summary>The buffs to take off and put on so the client shows what applies at <paramref name="now"/>: a kind
    /// whose strongest magnitude changed is sent again (the old one removed first, on the same instance); expired effects
    /// are dropped.</summary>
    public List<(Kind Kind, ushort? Removed, ushort? Added, int Magnitude)> Present(double now)
    {
        effects.RemoveAll(e => e.Until <= now);
        for (var kind = Kind.Chill; kind <= Kind.Shock; kind++)
            if (spread.TryGetValue(kind, out var copy) && copy.Until <= now) spread.Remove(kind);
        var changes = new List<(Kind, ushort?, ushort?, int)>();
        foreach (var kind in Enum.GetValues<Kind>())
        {
            int magnitude = (int)Magnitude(kind, now);
            bool had = shown.TryGetValue(kind, out int old);
            if (had ? old == magnitude : magnitude == 0) continue;
            if (magnitude > 0) shown[kind] = magnitude;
            else shown.Remove(kind);
            changes.Add((kind, had ? Instance(kind) : null, magnitude > 0 ? Instance(kind) : null, magnitude));
        }
        return changes;
    }
}
