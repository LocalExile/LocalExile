using GameServer.Core.World;

// Intimidate and Unnerve from the player's hits (poedb Intimidate: "Intimidate Enemies for 4 seconds on Hit",
// "Intimidated enemies take 10% increased Attack Damage"; Grattus Signet: "Unnerved enemies take 10% increased Spell
// Damage"), Exposure and Withered. Each lasts from the hit that inflicts it, a later one restarting it;
// EnemyDebuffsTick shows the buff.
public static partial class Game
{
    public const double HitDebuffSeconds = 4;
    public const int HitDebuffTakenPct = 10;
    // BuffDefinitions intimidated and unnerved; neither is IsRecovery, so 0x1c1 carries no values.
    public const ushort IntimidatedBuffRow = 733, UnnervedBuffRow = 1412;
    // Crush (Awakened Brutality: "10% chance to Crush Enemies for 4 seconds on Hit"): its own buff, and 15% off the
    // monster's physical damage reduction for those 4 seconds.
    public const ushort CrushedBuffRow = 1600;
    public const double CrushSeconds = 4;
    public const int CrushReductionPct = 15;
    // Maim (Maim Support: "30% chance to Maim on Hit"; Maimed enemies have 30% reduced Movement Speed): its own
    // BuffDefinitions row and four seconds. The support's physical damage taken is a separate timed property; plain
    // Maim only changes movement.
    public const ushort MaimedBuffRow = 513;
    public const double MaimSeconds = 4;
    // Hinder ("X% chance to Hinder Enemies on Hit with Spells"; ReminderTextHinder: 4 seconds, 30% reduced Movement
    // Speed): the client's own BuffDefinitions row (505) and the walk factor MonsterAilments applies while it runs.
    internal const ushort HinderedBuffRow = 505;
    internal const double HinderSeconds = 4;
    // Covered in Ash (BuffDefinitions 807, cover_in_ash): 20% less Movement Speed and 20% increased Fire Damage taken.
    public const ushort CoveredInAshBuffRow = 807;
    public const int CoveredInAshFireTakenPct = 20;

    /// <summary>Whether a hit intimidates and unnerves: chance_to_intimidate_on_hit_% (and attacks_intimidate_on_hit_%
    /// for an attack), chance_to_unnerve_on_hit_% (and chance_to_unnerve_on_hit_with_spells_% for a spell), and Grattus
    /// Signet's crits: spells_inflict_intimidate_on_crit, attacks_inflict_unnerve_on_crit.</summary>
    public static (bool Intimidate, bool Unnerve) HitDebuffRolls(Func<uint, int> get, bool spell, bool critical, Random random)
    {
        bool intimidate = spell && critical && get(Stat.SpellsInflictIntimidateOnCrit) != 0
            || RandomRolls.Chance(get(Stat.ChanceToIntimidateOnHitPct) + (spell ? 0 : get(Stat.AttacksIntimidateOnHitPct)), 100, random);
        bool unnerve = !spell && critical && get(Stat.AttacksInflictUnnerveOnCrit) != 0
            || RandomRolls.Chance(get(Stat.ChanceToUnnerveOnHitPct) + (spell ? get(Stat.ChanceToUnnerveOnHitWithSpellsPct) : 0), 100, random);
        return (intimidate, unnerve);
    }

    /// <summary>Whether a hit blinds: global_chance_to_blind_on_hit_% (the Blind support's 10%), attacks_chance_to_blind_on_hit_%
    /// for an attack and %_chance_to_blind_on_critical_strike on a critical strike, one roll for their sum.</summary>
    public static bool BlindRoll(Func<uint, int> get, bool spell, bool critical, Random random) =>
        RandomRolls.Chance(get(Stat.GlobalChanceToBlindOnHitPct) + (spell ? 0 : get(Stat.AttacksChanceToBlindOnHitPct))
            + (critical ? get(Stat.PctChanceToBlindOnCriticalStrike) : 0), 100, random);

    /// <summary>A Blind's duration: 4 s base (poedb Blind), with blind_duration_+%.</summary>
    public static double BlindSeconds(Func<uint, int> get) =>
        HitDebuffSeconds * Math.Max(0, 1 + get(Stat.BlindDurationPlusPct) / 100.0);

    // A hit that landed on a living hostile monster, in the area's frame. <paramref name="random"/> belongs to the
    // hit so projectile draws stay in order; others pass AreaRng(player).
    public static void HitDebuffsOnHitLocked(SpawnedMonster m, Func<uint, int> get, bool spell, bool critical, double now,
        Random random)
    {
        if (m.Hostile) ApplyContactDebuffsLocked(m, get, spell, critical, now, random);
    }

    static void ApplyContactDebuffsLocked(SpawnedMonster m, Func<uint, int> get, bool spell, bool critical, double now,
        Random random)
    {
        if (m.Dead || m.Life == 0) return;
        var (intimidate, unnerve) = HitDebuffRolls(get, spell, critical, random);
        if (intimidate) { m.IntimidatedUntil = Math.Max(m.IntimidatedUntil, now + HitDebuffSeconds); m.PresentationChanged?.Invoke(); }
        if (unnerve) { m.UnnervedUntil = Math.Max(m.UnnervedUntil, now + HitDebuffSeconds); m.PresentationChanged?.Invoke(); }
        if (RandomRolls.Chance(get(Stat.ChanceToCrushOnHitPct), 100, random))
            { m.CrushedUntil = Math.Max(m.CrushedUntil, now + CrushSeconds); m.PresentationChanged?.Invoke(); }
        TryApplyMaimLocked(m, get(Stat.MaimOnHitPct),
            Math.Max(0, get(Stat.SupportMaimedEnemiesPhysicalDamageTakenPlusPct)), now, random);
        if (spell && RandomRolls.Chance(get(Stat.SpellsChanceToHinderOnHitPct), 100, random))
            { m.HinderedUntil = Math.Max(m.HinderedUntil, now + HinderSeconds); m.PresentationChanged?.Invoke(); }
    }

    /// <summary>The shared Maim application for a surviving hostile hit. RandomRolls.Chance returns false without drawing
    /// when the chance is at most zero, and true without drawing when it is at least the scale.</summary>
    public static bool TryMaimOnHitLocked(SpawnedMonster m, int chance, int physicalTaken, double now, Random random) =>
        m.Hostile && TryApplyMaimLocked(m, chance, physicalTaken, now, random);

    static bool TryApplyMaimLocked(SpawnedMonster m, int chance, int physicalTaken, double now, Random random)
    {
        if (m.Dead || m.Life == 0) return false;
        if (m.Effects[Stat.ImmuneToMaim] != 0 || RandomRolls.Chance(m.Effects[Stat.AvoidMaimPctChance], 100, random))
            return false;
        if (!RandomRolls.Chance(chance, 100, random)) return false;
        // Settle elapsed bleed damage at its Rupture deadlines using the target's previous Maim.
        if (physicalTaken > 0 && m.Bleeds.Burning) SettleBleeding(m, now);
        m.MaimedUntil = Math.Max(m.MaimedUntil, now + MaimSeconds);
        m.PresentationChanged?.Invoke();
        physicalTaken = Math.Max(0, physicalTaken);
        if (physicalTaken > 0 && (physicalTaken >= m.MaimedPhysicalTakenPct || m.MaimedPhysicalTakenUntil <= now))
        {
            m.MaimedPhysicalTakenPct = physicalTaken;
            m.MaimedPhysicalTakenUntil = now + MaimSeconds;
        }
        return true;
    }

    /// <summary>Whether a timed movement debuff is running, so an otherwise idle monster still needs its walk factor
    /// recomputed (the ailment-idle skip must not starve Maim, Hinder or Covered in Ash).</summary>
    internal static bool TimedWalkDebuff(double now, SpawnedMonster m) => m.MaimedUntil > now || m.HinderedUntil > now || m.CoveredInAshUntil > now;

    /// <summary>The increased fire damage Covered in Ash applies at <paramref name="now"/> (20% while active, 0 otherwise).</summary>
    public static int CoveredInAshFireTaken(SpawnedMonster m, double now) => m.CoveredInAshUntil > now ? CoveredInAshFireTakenPct : 0;

    /// <summary>The physical damage reduction Crush takes away from a monster at <paramref name="now"/> (15% while
    /// crushed, 0 otherwise); without a time Crush does not count.</summary>
    public static int CrushReduction(SpawnedMonster m, double now) => m.CrushedUntil > now ? CrushReductionPct : 0;

    /// <summary>The increased physical damage a maimed monster takes from its actual support at <paramref name="now"/>;
    /// plain Maim gives 0.</summary>
    public static int MaimedPhysicalTaken(SpawnedMonster m, double now) =>
        m.MaimedUntil > now && m.MaimedPhysicalTakenUntil > now ? m.MaimedPhysicalTakenPct : 0;

    // Exposure (poedb Exposure): an unspecified value "defaults to -10% to the affected resistance", a duration "defaults to
    // 4 seconds", and "only the highest Exposure of the element applies". BuffDefinitions Fire, Cold and Lightning
    // Exposure (reduced_*_resistance_from_skill); none is IsRecovery, so 0x1c1 carries no values.
    public const int DefaultExposure = -10;
    public const double ExposureSeconds = 4;
    public static readonly DamageType[] ExposureTypes = [DamageType.Fire, DamageType.Cold, DamageType.Lightning];
    public static readonly ushort[] ExposureBuffRows = [1268, 1269, 1270];
    // Elemental Equilibrium (poedb: "Exposure inflicted this way applies -25% to Resistances").
    public const int ElementalEquilibriumExposure = -25;

    /// <summary>The Exposure of one element a hit inflicts, 0 for none. {element}_exposure_on_hit_magnitude always
    /// inflicts its own; inflict_all_exposure_on_hit, base_inflict_{element}_exposure_on_hit_%_chance and, for a hit with
    /// that damage, chance_to_inflict_{element}_exposure_on_hit_with_{element}_damage_% inflict the default -10%. The
    /// stronger one counts (<see cref="InflictedExposure"/>).</summary>
    public static int ExposureRoll(Func<uint, int> get, DamageType type, bool withType, Random random)
    {
        string e = type.ToString().ToLowerInvariant();
        bool rolled = get(Stat.InflictAllExposureOnHit) != 0 || RandomRolls.Chance(get(Stat.Of($"base_inflict_{e}_exposure_on_hit_%_chance"))
            + (withType ? get(Stat.Of($"chance_to_inflict_{e}_exposure_on_hit_with_{e}_damage_%")) : 0), 100, random);
        int magnitude = Math.Min(rolled ? DefaultExposure : 0, get(Stat.Of($"{e}_exposure_on_hit_magnitude")));
        return magnitude >= 0 ? 0 : InflictedExposure(get, type, magnitude);
    }

    /// <summary>The player's Exposure of this base magnitude as he inflicts it: plus the extra stats, at least
    /// exposure_you_inflict_has_minimum_resistance_%, none with cannot_inflict_exposure.</summary>
    public static int InflictedExposure(Func<uint, int> get, DamageType type, int magnitude)
    {
        if (get(Stat.CannotInflictExposure) != 0) return 0;
        string e = type.ToString().ToLowerInvariant();
        magnitude += get(Stat.Of($"{e}_exposure_you_inflict_applies_extra_{e}_resistance_+%"))
            + get(Stat.ExposureYouInflictAppliesExtraPctToAffectedResistance);
        int minimum = get(Stat.ExposureYouInflictHasMinimumResistancePct);
        return Math.Min(0, minimum < 0 ? Math.Min(magnitude, minimum) : magnitude);
    }

    // A hit that landed on a living monster, in the area's frame: each element's Exposure for 4 s, a later one of the
    // same magnitude restarting it. Then Elemental Equilibrium (poedb: "Hits that deal Elemental Damage remove Exposure to
    // those Elements and inflict Exposure to other Elements"), after the hit's own Exposure as in PoB, for 4 s too:
    // Exposure's default duration, as the keystone names none.
    public static void ExposureOnHitLocked(SpawnedMonster m, Func<uint, int> get, DamagePacket mitigated, double now, Random random)
    {
        if (m.Mods.Effects[Stat.ImmuneToExposure] > 0) return;
        for (int i = 0; i < ExposureTypes.Length; i++)
            if (ExposureRoll(get, ExposureTypes[i], mitigated[ExposureTypes[i]].Max > 0, random) is < 0 and var magnitude)
                Expose(m, i, magnitude, now);
        if (get(Stat.KeystoneElementalEquilibrium) == 0 || !ExposureTypes.Any(t => mitigated[t].Max > 0)) return;
        double effect = Math.Max(0, 1 + get(Stat.ElementalEquilibriumEffectPlusPct) / 100.0);
        for (int i = 0; i < ExposureTypes.Length; i++)
        {
            var type = ExposureTypes[i];
            if (mitigated[type].Max > 0)
            {
                EndExposureLocked(m, type);
                m.ExposureUntil[i] = double.NegativeInfinity;
            }
            else if (InflictedExposure(get, type, (int)(ElementalEquilibriumExposure * effect)) is < 0 and var magnitude)
                Expose(m, i, magnitude, now);
        }
    }

    static void Expose(SpawnedMonster m, int i, int magnitude, double now)
    {
        var key = (ExposureTypes[i], magnitude);
        double until = now + ExposureSeconds;
        m.Exposures[key] = Math.Max(m.Exposures.GetValueOrDefault(key, double.NegativeInfinity), until);
        { m.ExposureUntil[i] = Math.Max(m.ExposureUntil[i], until); m.PresentationChanged?.Invoke(); }
    }

    /// <summary>The resistance its Exposure of this type applies at <paramref name="now"/>: the highest running one, 0
    /// without a time.</summary>
    public static int MonsterExposure(SpawnedMonster m, DamageType type, double now)
    {
        int strongest = 0;
        foreach (var ((t, magnitude), until) in m.Exposures)
            if (t == type && until > now) strongest = Math.Min(strongest, magnitude);
        return strongest;
    }

    // Its Exposure of this type ended (EnemyDebuffsTick: it ran out, or it or the player died).
    public static void EndExposureLocked(SpawnedMonster m, DamageType type)
    {
        if (m.Exposures.Count == 0) return;
        foreach (var key in m.Exposures.Keys.Where(k => k.Type == type).ToArray()) m.Exposures.Remove(key);
    }

    // Withered (poedb Withered: "6% increased chaos damage taken on the affected target, stacking up to 15 times"; most
    // sources inflict it "for 2 seconds"). Each stack keeps its own end, and a 16th replaces the one ending first.
    // BuffDefinitions withered is IsRecovery, with chaos_damage_taken_+% and is_withered.
    public const double WitheredPct = 6;
    public const int WitheredMaxStacks = 15;
    public const ushort WitheredBuffRow = 506;

    public static double WitherDurationSeconds(double baseSeconds, Func<uint, int> get) =>
        baseSeconds / Math.Max(0.01, 1 + get(Stat.WitherExpireSpeedPlusPct) / 100.0);

    /// <summary>How long a hit withers for, 0 for not: one roll for withered_on_hit_for_2_seconds_%_chance and Withering
    /// Touch's withered_on_hit_chance_%, for its support_withered_base_duration_ms or else 2 s, expiring slower by
    /// wither_expire_speed_+% (the Eldritch gloves' "Withered you Inflict expires X% slower").</summary>
    public static double WitherRoll(Func<uint, int> get, Random random)
    {
        if (!RandomRolls.Chance(get(Stat.WitheredOnHitFor2SecondsPctChance) + get(Stat.WitheredOnHitChancePct), 100, random)) return 0;
        double seconds = get(Stat.SupportWitheredBaseDurationMs) is > 0 and var ms ? ms / 1000.0 : 2;
        return WitherDurationSeconds(seconds, get);
    }

    // A hit that landed on a living monster, in the area's frame: one more Withered stack, with withered_effect_+%.
    public static void WitherOnHitLocked(SpawnedMonster m, Func<uint, int> get, double now, Random random)
    {
        if (m.Mods.Effects[Stat.ImmuneToWither] > 0 || WitherRoll(get, random) is not (> 0 and var seconds)) return;
        ApplyWitherStacksLocked(m, 1, seconds, get, now);
    }

    // Adds <paramref name="stacks"/> Withered stacks with already-scaled duration <paramref name="seconds"/>
    // (capped at WitheredMaxStacks = 15, replacing the earliest-ending stack).
    public static void ApplyWitherStacksLocked(SpawnedMonster m, int stacks, double seconds, Func<uint, int> get, double now)
    {
        if (stacks <= 0 || seconds <= 0 || m.Mods.Effects[Stat.ImmuneToWither] > 0) return;
        double pct = WitheredPct * Math.Max(0, 1 + get(Stat.WitheredEffectPlusPct) / 100.0);
        m.Withered.RemoveAll(s => s.Until <= now);
        for (int i = 0; i < stacks; i++)
        {
            if (m.Withered.Count >= WitheredMaxStacks) m.Withered.Remove(m.Withered.MinBy(s => s.Until));
            m.Withered.Add((now + seconds, pct));
        }
        m.PresentationChanged?.Invoke();
    }

    /// <summary>The increased chaos damage its running Withered stacks give at <paramref name="now"/>, 0 without a time.</summary>
    public static double WitheredTaken(SpawnedMonster m, double now)
    {
        double taken = 0;
        foreach (var (until, pct) in m.Withered)
            if (until > now) taken += pct;
        return taken;
    }

    /// <summary>The increased damage a hit takes from them at <paramref name="now"/>: Intimidate's on an attack, Unnerve's
    /// on a spell.</summary>
    public static int HitDebuffTaken(SpawnedMonster m, bool spell, double now) =>
        (spell ? m.UnnervedUntil > now : m.IntimidatedUntil > now
            || m.PlayerAuras.Keys.Any(key => key.Definition == IntimidatedBuffRow)) ? HitDebuffTakenPct : 0;
}
