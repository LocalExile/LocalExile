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
    /// <paramref name="ruled"/>: the stats an effect rule is live for on the hitting character (EffectRules.LiveFor); the
    /// rule rolls those, so they are left out here.
    public static (bool Intimidate, bool Unnerve) HitDebuffRolls(Func<uint, int> get, bool spell, bool critical, Random random,
        Func<uint, bool>? ruled = null)
    {
        bool Ruled(uint stat) => ruled is not null && ruled(stat);
        bool intimidate = spell && critical && !Ruled(Stat.SpellsInflictIntimidateOnCrit) && get(Stat.SpellsInflictIntimidateOnCrit) != 0
            || !Ruled(Stat.ChanceToIntimidateOnHitPct)
                && RandomRolls.Chance(get(Stat.ChanceToIntimidateOnHitPct) + (spell ? 0 : get(Stat.AttacksIntimidateOnHitPct)), 100, random);
        bool unnerve = !spell && critical && !Ruled(Stat.AttacksInflictUnnerveOnCrit) && get(Stat.AttacksInflictUnnerveOnCrit) != 0
            || !Ruled(Stat.ChanceToUnnerveOnHitPct)
                && RandomRolls.Chance(get(Stat.ChanceToUnnerveOnHitPct) + (spell ? get(Stat.ChanceToUnnerveOnHitWithSpellsPct) : 0), 100, random);
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
        Random random, Func<uint, bool>? ruled = null)
    {
        if (m.Hostile) ApplyContactDebuffsLocked(m, get, spell, critical, now, random, ruled);
    }

    /// <summary>The debuffs the effect rules' "debuff" can put on the monster of a hit.</summary>
    public static bool NamedDebuff(string debuff) => debuff is "intimidated" or "unnerved" or "crushed" or "hindered"
        or "maimed" or "blinded" or "withered" or "fire_exposure" or "cold_exposure" or "lightning_exposure";

    /// <summary>What the effect rules' "debuff" does to the monster of a hit: one of the debuffs below, from
    /// <paramref name="now"/>, a later one restarting it. The rule has rolled its chance; what the debuff itself asks of
    /// the monster (immunity, a chance to avoid it) and what the hitting side's other stats do to it (duration, effect,
    /// magnitude: <paramref name="get"/>, the character's stats with the skill's) is worked out here, by the code the
    /// hit used before. <paramref name="seconds"/> is the rule's base duration and <paramref name="amount"/> its base
    /// magnitude (an Exposure's resistance). <paramref name="id"/>: the monster's id in its area, 0 where the hit's site
    /// has none (Blind needs it). False for a name that is not one of them.</summary>
    public static bool ApplyNamedDebuffLocked(Player player, SpawnedMonster m, uint id, string debuff, double seconds, double amount,
        double now, Func<uint, int> get)
    {
        // An invulnerable monster's hit raises the event too, and the code never put a hit's debuffs on it.
        if (!m.Hostile || m.Dead || m.Life == 0 || m.Invulnerable) return true;
        switch (debuff)
        {
            case "intimidated": m.IntimidatedUntil = Math.Max(m.IntimidatedUntil, now + seconds); break;
            case "unnerved": m.UnnervedUntil = Math.Max(m.UnnervedUntil, now + seconds); break;
            case "crushed": m.CrushedUntil = Math.Max(m.CrushedUntil, now + seconds); break;
            case "hindered": m.HinderedUntil = Math.Max(m.HinderedUntil, now + seconds); break;
            // Maim: the monster's immunity and its chance to avoid it, and the Maim support's physical damage taken.
            case "maimed":
                TryApplyMaimLocked(m, 100, Math.Max(0, get(Stat.SupportMaimedEnemiesPhysicalDamageTakenPlusPct)), now, AreaRng(player), seconds);
                return true;
            // Blind: blind_duration_+% and blind_effect_+%; the blind itself is the character's (cannot_be_blinded there).
            case "blinded":
                if (id != 0) InflictMonsterBlind(player, id, m, now,
                    (100, get(Stat.BlindEffectPlusPct), seconds * Math.Max(0, 1 + get(Stat.BlindDurationPlusPct) / 100.0)));
                return true;
            // Withered: one more stack. A skill's support_withered_base_duration_ms is the duration in place of the
            // rule's; wither_expire_speed_+% and withered_effect_+% as for any stack.
            case "withered":
                double baseSeconds = get(Stat.SupportWitheredBaseDurationMs) is > 0 and var ms ? ms / 1000.0 : seconds;
                ApplyWitherStacksLocked(m, 1, WitherDurationSeconds(baseSeconds, get), get, now);
                return true;
            // Exposure: the rule's magnitude with the stats that add to an Exposure the character inflicts.
            case "fire_exposure" or "cold_exposure" or "lightning_exposure":
                int element = debuff[0] switch { 'f' => 0, 'c' => 1, _ => 2 };
                if (m.Mods.Effects[Stat.ImmuneToExposure] > 0 || amount >= 0) return true;
                if (InflictedExposure(get, ExposureTypes[element], (int)Math.Round(amount)) is < 0 and var magnitude)
                    Expose(m, element, magnitude, now, seconds);
                return true;
            default: return false;
        }
        m.PresentationChanged?.Invoke();
        return true;
    }

    static void ApplyContactDebuffsLocked(SpawnedMonster m, Func<uint, int> get, bool spell, bool critical, double now,
        Random random, Func<uint, bool>? ruled = null)
    {
        if (m.Dead || m.Life == 0) return;
        bool Ruled(uint stat) => ruled is not null && ruled(stat);
        var (intimidate, unnerve) = HitDebuffRolls(get, spell, critical, random, ruled);
        if (intimidate) { m.IntimidatedUntil = Math.Max(m.IntimidatedUntil, now + HitDebuffSeconds); m.PresentationChanged?.Invoke(); }
        if (unnerve) { m.UnnervedUntil = Math.Max(m.UnnervedUntil, now + HitDebuffSeconds); m.PresentationChanged?.Invoke(); }
        if (!Ruled(Stat.ChanceToCrushOnHitPct) && RandomRolls.Chance(get(Stat.ChanceToCrushOnHitPct), 100, random))
            { m.CrushedUntil = Math.Max(m.CrushedUntil, now + CrushSeconds); m.PresentationChanged?.Invoke(); }
        if (!Ruled(Stat.MaimOnHitPct))
            TryApplyMaimLocked(m, get(Stat.MaimOnHitPct),
                Math.Max(0, get(Stat.SupportMaimedEnemiesPhysicalDamageTakenPlusPct)), now, random);
        if (spell && !Ruled(Stat.SpellsChanceToHinderOnHitPct) && RandomRolls.Chance(get(Stat.SpellsChanceToHinderOnHitPct), 100, random))
            { m.HinderedUntil = Math.Max(m.HinderedUntil, now + HinderSeconds); m.PresentationChanged?.Invoke(); }
    }

    /// <summary>The shared Maim application for a surviving hostile hit. RandomRolls.Chance returns false without drawing
    /// when the chance is at most zero, and true without drawing when it is at least the scale.</summary>
    public static bool TryMaimOnHitLocked(SpawnedMonster m, int chance, int physicalTaken, double now, Random random) =>
        m.Hostile && TryApplyMaimLocked(m, chance, physicalTaken, now, random);

    static bool TryApplyMaimLocked(SpawnedMonster m, int chance, int physicalTaken, double now, Random random,
        double seconds = MaimSeconds)
    {
        if (m.Dead || m.Life == 0) return false;
        if (m.Effects[Stat.ImmuneToMaim] != 0 || RandomRolls.Chance(m.Effects[Stat.AvoidMaimPctChance], 100, random))
            return false;
        if (!RandomRolls.Chance(chance, 100, random)) return false;
        // Settle elapsed bleed damage at its Rupture deadlines using the target's previous Maim.
        if (physicalTaken > 0 && m.Bleeds.Burning) SettleBleeding(m, now);
        m.MaimedUntil = Math.Max(m.MaimedUntil, now + seconds);
        m.PresentationChanged?.Invoke();
        physicalTaken = Math.Max(0, physicalTaken);
        if (physicalTaken > 0 && (physicalTaken >= m.MaimedPhysicalTakenPct || m.MaimedPhysicalTakenUntil <= now))
        {
            m.MaimedPhysicalTakenPct = physicalTaken;
            m.MaimedPhysicalTakenUntil = now + seconds;
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
    /// <paramref name="ruled"/>: the stats an effect rule is live for on the hitting character; the rule inflicts
    /// those, so they are left out here.
    public static int ExposureRoll(Func<uint, int> get, DamageType type, bool withType, Random random, Func<uint, bool>? ruled = null)
    {
        string e = type.ToString().ToLowerInvariant();
        bool Ruled(uint stat) => ruled is not null && ruled(stat);
        uint chance = Stat.Of($"base_inflict_{e}_exposure_on_hit_%_chance"), own = Stat.Of($"{e}_exposure_on_hit_magnitude");
        bool rolled = !Ruled(Stat.InflictAllExposureOnHit) && get(Stat.InflictAllExposureOnHit) != 0
            || !Ruled(chance) && RandomRolls.Chance(get(chance)
                + (withType ? get(Stat.Of($"chance_to_inflict_{e}_exposure_on_hit_with_{e}_damage_%")) : 0), 100, random);
        int magnitude = Math.Min(rolled ? DefaultExposure : 0, Ruled(own) ? 0 : get(own));
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
    public static void ExposureOnHitLocked(SpawnedMonster m, Func<uint, int> get, DamagePacket mitigated, double now, Random random,
        Func<uint, bool>? ruled = null)
    {
        if (m.Mods.Effects[Stat.ImmuneToExposure] > 0) return;
        for (int i = 0; i < ExposureTypes.Length; i++)
            if (ExposureRoll(get, ExposureTypes[i], mitigated[ExposureTypes[i]].Max > 0, random, ruled) is < 0 and var magnitude)
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

    static void Expose(SpawnedMonster m, int i, int magnitude, double now, double seconds = ExposureSeconds)
    {
        var key = (ExposureTypes[i], magnitude);
        double until = now + seconds;
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
    public static double WitherRoll(Func<uint, int> get, Random random, Func<uint, bool>? ruled = null)
    {
        // The rule rolls the two chances as one and adds the stack (EffectRules.LiveFor).
        if (ruled is not null && ruled(Stat.WitheredOnHitFor2SecondsPctChance)) return 0;
        if (!RandomRolls.Chance(get(Stat.WitheredOnHitFor2SecondsPctChance) + get(Stat.WitheredOnHitChancePct), 100, random)) return 0;
        double seconds = get(Stat.SupportWitheredBaseDurationMs) is > 0 and var ms ? ms / 1000.0 : 2;
        return WitherDurationSeconds(seconds, get);
    }

    // A hit that landed on a living monster, in the area's frame: one more Withered stack, with withered_effect_+%.
    public static void WitherOnHitLocked(SpawnedMonster m, Func<uint, int> get, double now, Random random, Func<uint, bool>? ruled = null)
    {
        if (m.Mods.Effects[Stat.ImmuneToWither] > 0 || WitherRoll(get, random, ruled) is not (> 0 and var seconds)) return;
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

    /// <summary>The increased chaos damage from active Withered stacks at <paramref name="now"/>.</summary>
    public static double WitheredTaken(SpawnedMonster m, double now) => ReadWithered(m, now).Taken;

    // A Withering pod contributes once while an enemy is in its radius. It shares the timed stacks' cap, and
    // leaving removes that pod immediately without changing any of the Withered stacks from hits or other skills.
    static (int Count, double Taken, double Until, double DisplayPct) ReadWithered(SpawnedMonster m, double now)
    {
        int count = 0;
        double taken = 0, latest = 0, display = 0;
        foreach (var (until, pct) in m.Withered)
        {
            if (until <= now) continue;
            count++;
            taken += pct;
            latest = Math.Max(latest, until);
            display = pct;
        }
        if (count < WitheredMaxStacks && ToxicVines.TryGetValue(m, out var pods) && m.Mods.Effects[Stat.ImmuneToWither] == 0)
            lock (pods)
                foreach (var pod in pods)
                {
                    if (pod.Withered is not double pct || pod.Until <= now) continue;
                    count++;
                    taken += pct;
                    latest = Math.Max(latest, pod.Until);
                    display = pct;
                    if (count == WitheredMaxStacks) break;
                }
        return (count, taken, latest, display);
    }

    /// <summary>The increased damage a hit takes from them at <paramref name="now"/>: Intimidate's on an attack, Unnerve's
    /// on a spell.</summary>
    public static int HitDebuffTaken(SpawnedMonster m, bool spell, double now) =>
        (spell ? m.UnnervedUntil > now : m.IntimidatedUntil > now
            || m.PlayerAuras.Keys.Any(key => key.Definition == IntimidatedBuffRow)) ? HitDebuffTakenPct : 0;
}
