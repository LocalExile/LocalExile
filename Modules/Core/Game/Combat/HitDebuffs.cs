using GameServer.Core.World;

// Intimidate and Unnerve from the player's hits (poedb Intimidate: "Intimidate Enemies for 4 seconds on Hit",
// "Intimidated enemies take 10% increased Attack Damage"; Grattus Signet: "Unnerved enemies take 10% increased Spell
// Damage"), Exposure and Withered. Each lasts from the hit that inflicts it, a later one restarting it;
// EnemyDebuffsTick shows the buff.
static partial class Game
{
    internal const double HitDebuffSeconds = 4;
    internal const int HitDebuffTakenPct = 10;
    // BuffDefinitions intimidated and unnerved; neither is IsRecovery, so 0x1c1 carries no values.
    internal const ushort IntimidatedBuffRow = 733, UnnervedBuffRow = 1412;
    // Crush (Awakened Brutality: "10% chance to Crush Enemies for 4 seconds on Hit"): its own buff, and 15% off the
    // monster's physical damage reduction for those 4 seconds.
    internal const ushort CrushedBuffRow = 1600;
    internal const double CrushSeconds = 4;
    internal const int CrushReductionPct = 15;
    // Maim (Maim Support: "30% chance to Maim on Hit"; Maimed enemies have 30% reduced Movement Speed): its own
    // BuffDefinitions row and four seconds. The support's physical damage taken is a separate timed property; plain
    // Maim only changes movement.
    internal const ushort MaimedBuffRow = 513;
    internal const double MaimSeconds = 4;

    /// <summary>Whether a hit intimidates and unnerves: chance_to_intimidate_on_hit_% (and attacks_intimidate_on_hit_%
    /// for an attack), chance_to_unnerve_on_hit_% (and chance_to_unnerve_on_hit_with_spells_% for a spell), and Grattus
    /// Signet's crits: spells_inflict_intimidate_on_crit, attacks_inflict_unnerve_on_crit.</summary>
    internal static (bool Intimidate, bool Unnerve) HitDebuffRolls(Func<string, int> get, bool spell, bool critical, Random random)
    {
        bool intimidate = spell && critical && get("spells_inflict_intimidate_on_crit") != 0
            || RandomRolls.Chance(get("chance_to_intimidate_on_hit_%") + (spell ? 0 : get("attacks_intimidate_on_hit_%")), 100, random);
        bool unnerve = !spell && critical && get("attacks_inflict_unnerve_on_crit") != 0
            || RandomRolls.Chance(get("chance_to_unnerve_on_hit_%") + (spell ? get("chance_to_unnerve_on_hit_with_spells_%") : 0), 100, random);
        return (intimidate, unnerve);
    }

    /// <summary>A Blind's duration: 4 s base (poedb Blind), with blind_duration_+%.</summary>
    internal static double BlindSeconds(Func<string, int> get) =>
        HitDebuffSeconds * Math.Max(0, 1 + get("blind_duration_+%") / 100.0);

    // A hit that landed on a living hostile monster, under the Monsters lock. <paramref name="random"/> belongs to the
    // hit so projectile draws stay in order; callers without one use Random.Shared.
    internal static void HitDebuffsOnHitLocked(SpawnedMonster m, Func<string, int> get, bool spell, bool critical, double now,
        Random? random = null)
    {
        if (!m.Hostile || m.Dead || m.Life == 0) return;
        random ??= Random.Shared;
        var (intimidate, unnerve) = HitDebuffRolls(get, spell, critical, random);
        if (intimidate) m.IntimidatedUntil = Math.Max(m.IntimidatedUntil, now + HitDebuffSeconds);
        if (unnerve) m.UnnervedUntil = Math.Max(m.UnnervedUntil, now + HitDebuffSeconds);
        if (RandomRolls.Chance(get("chance_to_crush_on_hit_%"), 100, random))
            m.CrushedUntil = Math.Max(m.CrushedUntil, now + CrushSeconds);
        TryMaimOnHitLocked(m, get("maim_on_hit_%"),
            Math.Max(0, get("support_maimed_enemies_physical_damage_taken_+%")), now, random);
    }

    /// <summary>The shared Maim application for a surviving hostile hit. RandomRolls.Chance returns false without drawing
    /// when the chance is at most zero, and true without drawing when it is at least the scale.</summary>
    internal static bool TryMaimOnHitLocked(SpawnedMonster m, int chance, int physicalTaken, double now, Random random)
    {
        if (!m.Hostile || m.Dead || m.Life == 0) return false;
        if (m.Effects["immune_to_maim"] != 0 || RandomRolls.Chance(m.Effects["avoid_maim_%_chance"], 100, random))
            return false;
        if (!RandomRolls.Chance(chance, 100, random)) return false;
        m.MaimedUntil = Math.Max(m.MaimedUntil, now + MaimSeconds);
        physicalTaken = Math.Max(0, physicalTaken);
        if (physicalTaken > 0 && (physicalTaken >= m.MaimedPhysicalTakenPct || m.MaimedPhysicalTakenUntil <= now))
        {
            m.MaimedPhysicalTakenPct = physicalTaken;
            m.MaimedPhysicalTakenUntil = now + MaimSeconds;
        }
        return true;
    }

    /// <summary>The physical damage reduction Crush takes away from a monster at <paramref name="now"/> (15% while
    /// crushed, 0 otherwise); without a time Crush does not count.</summary>
    internal static int CrushReduction(SpawnedMonster m, double now) => m.CrushedUntil > now ? CrushReductionPct : 0;

    /// <summary>The increased physical damage a maimed monster takes from its actual support at <paramref name="now"/>;
    /// plain Maim gives 0.</summary>
    internal static int MaimedPhysicalTaken(SpawnedMonster m, double now) =>
        m.MaimedUntil > now && m.MaimedPhysicalTakenUntil > now ? m.MaimedPhysicalTakenPct : 0;

    // Exposure (poedb Exposure): an unspecified value "defaults to -10% to the affected resistance", a duration "defaults to
    // 4 seconds", and "only the highest Exposure of the element applies". BuffDefinitions Fire, Cold and Lightning
    // Exposure (reduced_*_resistance_from_skill); none is IsRecovery, so 0x1c1 carries no values.
    internal const int DefaultExposure = -10;
    internal const double ExposureSeconds = 4;
    internal static readonly DamageType[] ExposureTypes = [DamageType.Fire, DamageType.Cold, DamageType.Lightning];
    internal static readonly ushort[] ExposureBuffRows = [1268, 1269, 1270];
    // Elemental Equilibrium (poedb: "Exposure inflicted this way applies -25% to Resistances").
    internal const int ElementalEquilibriumExposure = -25;

    /// <summary>The Exposure of one element a hit inflicts, 0 for none. {element}_exposure_on_hit_magnitude always
    /// inflicts its own; inflict_all_exposure_on_hit, base_inflict_{element}_exposure_on_hit_%_chance and, for a hit with
    /// that damage, chance_to_inflict_{element}_exposure_on_hit_with_{element}_damage_% inflict the default -10%. The
    /// stronger one counts (<see cref="InflictedExposure"/>).</summary>
    internal static int ExposureRoll(Func<string, int> get, DamageType type, bool withType, Random random)
    {
        string e = type.ToString().ToLowerInvariant();
        bool rolled = get("inflict_all_exposure_on_hit") != 0 || RandomRolls.Chance(get($"base_inflict_{e}_exposure_on_hit_%_chance")
            + (withType ? get($"chance_to_inflict_{e}_exposure_on_hit_with_{e}_damage_%") : 0), 100, random);
        int magnitude = Math.Min(rolled ? DefaultExposure : 0, get($"{e}_exposure_on_hit_magnitude"));
        return magnitude >= 0 ? 0 : InflictedExposure(get, type, magnitude);
    }

    /// <summary>The player's Exposure of this base magnitude as he inflicts it: plus the extra stats, at least
    /// exposure_you_inflict_has_minimum_resistance_%, none with cannot_inflict_exposure.</summary>
    internal static int InflictedExposure(Func<string, int> get, DamageType type, int magnitude)
    {
        if (get("cannot_inflict_exposure") != 0) return 0;
        string e = type.ToString().ToLowerInvariant();
        magnitude += get($"{e}_exposure_you_inflict_applies_extra_{e}_resistance_+%")
            + get("exposure_you_inflict_applies_extra_%_to_affected_resistance");
        int minimum = get("exposure_you_inflict_has_minimum_resistance_%");
        return Math.Min(0, minimum < 0 ? Math.Min(magnitude, minimum) : magnitude);
    }

    // A hit that landed on a living monster, under the Monsters lock: each element's Exposure for 4 s, a later one of the
    // same magnitude restarting it. Then Elemental Equilibrium (poedb: "Hits that deal Elemental Damage remove Exposure to
    // those Elements and inflict Exposure to other Elements"), after the hit's own Exposure as in PoB, for 4 s too:
    // Exposure's default duration, as the keystone names none.
    internal static void ExposureOnHitLocked(SpawnedMonster m, Func<string, int> get, DamagePacket mitigated, double now, Random random)
    {
        if (m.Mods.Effects["immune_to_exposure"] > 0) return;
        for (int i = 0; i < ExposureTypes.Length; i++)
            if (ExposureRoll(get, ExposureTypes[i], mitigated[ExposureTypes[i]].Max > 0, random) is < 0 and var magnitude)
                Expose(m, i, magnitude, now);
        if (get("keystone_elemental_equilibrium") == 0 || !ExposureTypes.Any(t => mitigated[t].Max > 0)) return;
        double effect = Math.Max(0, 1 + get("elemental_equilibrium_effect_+%") / 100.0);
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
        m.ExposureUntil[i] = Math.Max(m.ExposureUntil[i], until);
    }

    /// <summary>The resistance its Exposure of this type applies at <paramref name="now"/>: the highest running one, 0
    /// without a time.</summary>
    internal static int MonsterExposure(SpawnedMonster m, DamageType type, double now)
    {
        int strongest = 0;
        foreach (var ((t, magnitude), until) in m.Exposures)
            if (t == type && until > now) strongest = Math.Min(strongest, magnitude);
        return strongest;
    }

    // Its Exposure of this type ended (EnemyDebuffsTick: it ran out, or it or the player died).
    internal static void EndExposureLocked(SpawnedMonster m, DamageType type)
    {
        if (m.Exposures.Count == 0) return;
        foreach (var key in m.Exposures.Keys.Where(k => k.Type == type).ToArray()) m.Exposures.Remove(key);
    }

    // Withered (poedb Withered: "6% increased chaos damage taken on the affected target, stacking up to 15 times"; most
    // sources inflict it "for 2 seconds"). Each stack keeps its own end, and a 16th replaces the one ending first.
    // BuffDefinitions withered is IsRecovery, with chaos_damage_taken_+% and is_withered.
    internal const double WitheredPct = 6;
    internal const int WitheredMaxStacks = 15;
    internal const ushort WitheredBuffRow = 506;

    /// <summary>How long a hit withers for, 0 for not: one roll for withered_on_hit_for_2_seconds_%_chance and Withering
    /// Touch's withered_on_hit_chance_%, for its support_withered_base_duration_ms or else 2 s, expiring slower by
    /// wither_expire_speed_+% (the Eldritch gloves' "Withered you Inflict expires X% slower").</summary>
    internal static double WitherRoll(Func<string, int> get, Random random)
    {
        if (!RandomRolls.Chance(get("withered_on_hit_for_2_seconds_%_chance") + get("withered_on_hit_chance_%"), 100, random)) return 0;
        double seconds = get("support_withered_base_duration_ms") is > 0 and var ms ? ms / 1000.0 : 2;
        return seconds / Math.Max(0.01, 1 + get("wither_expire_speed_+%") / 100.0);
    }

    // A hit that landed on a living monster, under the Monsters lock: one more Withered stack, with withered_effect_+%.
    internal static void WitherOnHitLocked(SpawnedMonster m, Func<string, int> get, double now, Random random)
    {
        if (m.Mods.Effects["immune_to_wither"] > 0 || WitherRoll(get, random) is not (> 0 and var seconds)) return;
        m.Withered.RemoveAll(s => s.Until <= now);
        if (m.Withered.Count >= WitheredMaxStacks) m.Withered.Remove(m.Withered.MinBy(s => s.Until));
        m.Withered.Add((now + seconds, WitheredPct * Math.Max(0, 1 + get("withered_effect_+%") / 100.0)));
    }

    /// <summary>The increased chaos damage its running Withered stacks give at <paramref name="now"/>, 0 without a time.</summary>
    internal static double WitheredTaken(SpawnedMonster m, double now)
    {
        double taken = 0;
        foreach (var (until, pct) in m.Withered)
            if (until > now) taken += pct;
        return taken;
    }

    /// <summary>The increased damage a hit takes from them at <paramref name="now"/>: Intimidate's on an attack, Unnerve's
    /// on a spell.</summary>
    internal static int HitDebuffTaken(SpawnedMonster m, bool spell, double now) =>
        (spell ? m.UnnervedUntil : m.IntimidatedUntil) > now ? HitDebuffTakenPct : 0;
}
