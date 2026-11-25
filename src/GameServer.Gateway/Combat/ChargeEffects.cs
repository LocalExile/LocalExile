/// <summary>
/// What the charges a character holds do to its stats (issue #237). The server works these out and sends final values:
/// the client derives none of them (11-skills.md §4).
/// <list type="bullet">
/// <item>Every character has the per-charge bonuses of Metadata/Characters/Character.ot (<see cref="Inherent"/>): 4%
/// increased attack and cast speed and 4% more damage per Frenzy Charge, 4% physical and 4% elemental damage reduction
/// per Endurance Charge, 50% increased critical strike chance per Power Charge. Path of Building's CalcSetup reads the
/// same constants.</item>
/// <item>Each "per charge" stat of passives, items and gems (Character.ot's included) becomes, × the charges held, the
/// stat the server already calculates with (<see cref="PerCharge"/>): the pools (<c>attack_speed_+%</c>,
/// <c>critical_strike_chance_+%</c>, resistances, armour, evasion, block, suppression, movement speed …), the hit
/// damage (increased damage, added damage, gain as extra chaos), regeneration, area, projectile speed, or a panel
/// sum.</item>
/// <item>The "more damage" and damage reduction ones have no plain stat to become: <see cref="DamageMore"/> scales hits
/// and burning, <see cref="Reduction"/> mitigates hits taken.</item>
/// </list>
/// The rows of the Charges tab read the per-charge stats themselves, and <c>current_*_charges</c> /
/// <c>virtual_maximum_*_charges</c> (<see cref="Display"/>). Under a "gain … instead of" flag
/// (<see cref="Charges.ConversionFlag"/>) a type's charges are Brutal, Affliction or Absorption Charges: the
/// standard effects stop, the special per-charge stats (<see cref="Rule.Special"/>) and <see cref="DamageMore"/>'s
/// ailment multiplier take the count, and the special rows show it.
/// </summary>
public static class ChargeEffects
{
    /// <summary>Metadata/Characters/Character.ot's per-charge stats (the Brine charge ones belong to another charge
    /// type and are left out). They are the base forms the panel rows sum and <see cref="PerCharge"/> reads.</summary>
    public static readonly IReadOnlyList<(uint Stat, int Value)> Inherent =
    [
        (Stat.BaseAttackSpeedPlusPctPerFrenzyCharge, 4),
        (Stat.BaseCastSpeedPlusPctPerFrenzyCharge, 4),
        (Stat.ObjectInherentDamagePlusPctFinalPerFrenzyCharge, 4),
        (Stat.PhysicalDamageReductionPctPerEnduranceCharge, 4),
        (Stat.ElementalDamageReductionPctPerEnduranceCharge, 4),
        (Stat.CriticalStrikeChancePlusPctPerPowerCharge, 50),
    ];

    /// <summary>One per-charge stat: × the charges of <paramref name="Type"/> held (null: all three types together), it
    /// adds to each of <paramref name="Targets"/>. <paramref name="Cap"/> limits the total ("up to 50%").
    /// <paramref name="Special"/>: a stat per the type's special charge (Brutal, Affliction, Absorption), which counts
    /// the type's charges only while they are converted (<see cref="Charges.ConversionFlag"/>). <paramref name="PerMaximum"/>:
    /// × the type's maximum instead of the charges held ("per maximum Power Charge").</summary>
    public sealed record Rule(uint Stat, ChargeType? Type, uint[] Targets, int? Cap = null, bool Special = false,
        bool PerMaximum = false);

    // The physical one goes into the passives' base form, which the virtual total and the hits read; the elemental one has
    // no base form.
    const uint PhysicalReduction = Stat.BaseAdditionalPhysicalDamageReductionPct;
    const uint AdditionalPhysicalReduction = Stat.AdditionalPhysicalDamageReductionPct;
    const uint AdditionalElementalReduction = Stat.AdditionalElementalDamageReductionPct;

    static Rule E(uint stat, params uint[] targets) => new(stat, ChargeType.Endurance, targets);
    static Rule F(uint stat, params uint[] targets) => new(stat, ChargeType.Frenzy, targets);
    static Rule P(uint stat, params uint[] targets) => new(stat, ChargeType.Power, targets);

    /// <summary>
    /// The per-charge stats the server applies, and the stats they add to. "Adds X to Y damage per charge" without "to
    /// attacks/spells" is global added damage: it adds to both (Path of Building maps it without a skill flag).
    /// Added damage has a minimum and a maximum stat, each its own rule. Targets the server does not calculate with
    /// (wand-only or attack-only damage, mana cost, buff duration) are left out and listed in 11-skills.md §15.
    /// </summary>
    public static readonly IReadOnlyList<Rule> PerCharge =
    [
        // Endurance
        E(Stat.PhysicalDamageReductionPctPerEnduranceCharge, PhysicalReduction),
        E(Stat.ElementalDamageReductionPctPerEnduranceCharge, AdditionalElementalReduction),
        E(Stat.ResistAllElementsPctPerEnduranceCharge, Stat.BaseResistAllElementsPct),
        E(Stat.ChaosDamageResistancePctPerEnduranceCharge, Stat.BaseChaosDamageResistancePct),
        E(Stat.LifeRegenerationRatePerMinutePctPerEnduranceCharge, Stat.LifeRegenerationRatePerMinutePct),
        E(Stat.LifeRegenPerMinutePerEnduranceCharge, Stat.BaseLifeRegenerationRatePerMinute),
        E(Stat.MeleeDamagePlusPctPerEnduranceCharge, Stat.MeleeDamagePlusPct),
        E(Stat.PhysicalDamagePerEnduranceChargePlusPct, Stat.PhysicalDamagePlusPct),
        E(Stat.DamagePlusPctPerEnduranceCharge, Stat.DamagePlusPct),
        E(Stat.AdditionalBlockPctPerEnduranceCharge, Stat.AdditionalBlockPct),
        E(Stat.AdditionalAttackBlockPctPerEnduranceCharge, Stat.AdditionalBlockPct),
        E(Stat.BaseMaxFortificationPerEnduranceCharge, Stat.BaseMaxFortification),
        E(Stat.PhysicalDamageReductionRatingPlusPctPerEnduranceCharge, Stat.PhysicalDamageReductionRatingPlusPct),
        E(Stat.PhysicalDamageReductionRatingPerEnduranceCharge, Stat.BasePhysicalDamageReductionRating),
        E(Stat.SpellSuppressionChancePctPerEnduranceCharge, Stat.BaseSpellSuppressionChancePct),
        E(Stat.MovementSpeedPlusPctPerEnduranceCharge, Stat.BaseMovementVelocityPlusPct),
        E(Stat.AttackAndCastSpeedPlusPctPerEnduranceCharge, Stat.AttackAndCastSpeedPlusPct),
        E(Stat.CriticalStrikeChancePlusPctPerEnduranceCharge, Stat.CriticalStrikeChancePlusPct),
        E(Stat.AreaOfEffectPlusPctPerEnduranceCharge, Stat.BaseSkillAreaOfEffectPlusPct),
        E(Stat.StunDurationPlusPctPerEnduranceCharge, Stat.BaseStunDurationPlusPct),
        E(Stat.MinimumAddedFireDamagePerEnduranceCharge, Stat.AttackMinimumAddedFireDamage, Stat.SpellMinimumAddedFireDamage),
        E(Stat.MaximumAddedFireDamagePerEnduranceCharge, Stat.AttackMaximumAddedFireDamage, Stat.SpellMaximumAddedFireDamage),
        E(Stat.MinimumAddedPhysicalDamagePerEnduranceCharge, Stat.AttackMinimumAddedPhysicalDamage, Stat.SpellMinimumAddedPhysicalDamage),
        E(Stat.MaximumAddedPhysicalDamagePerEnduranceCharge, Stat.AttackMaximumAddedPhysicalDamage, Stat.SpellMaximumAddedPhysicalDamage),
        E(Stat.FireDamagePctToAddAsChaosPerEnduranceCharge, Stat.FireDamagePctToAddAsChaos),
        // Frenzy
        F(Stat.BaseAttackSpeedPlusPctPerFrenzyCharge, Stat.AttackSpeedPlusPct),
        F(Stat.BaseCastSpeedPlusPctPerFrenzyCharge, Stat.BaseCastSpeedPlusPct),
        F(Stat.AttackAndCastSpeedPlusPctPerFrenzyCharge, Stat.AttackAndCastSpeedPlusPct),
        F(Stat.MovementVelocityPlusPctPerFrenzyCharge, Stat.BaseMovementVelocityPlusPct),
        F(Stat.StunRecoveryPlusPctPerFrenzyCharge, Stat.BaseStunRecoveryPlusPct),
        F(Stat.PhysicalDamagePlusPctPerFrenzyCharge, Stat.PhysicalDamagePlusPct),
        F(Stat.DamagePlusPctPerFrenzyCharge, Stat.DamagePlusPct),
        F(Stat.LightningDamagePlusPctPerFrenzyCharge, Stat.LightningDamagePlusPct),
        F(Stat.ColdDamagePlusPctPerFrenzyCharge, Stat.ColdDamagePlusPct),
        F(Stat.ElementalDamagePlusPctPerFrenzyCharge, Stat.ElementalDamagePlusPct),
        F(Stat.EvasionRatingPlusPctPerFrenzyCharge, Stat.EvasionRatingPlusPct),
        F(Stat.GlobalDefencesPlusPctPerFrenzyCharge, Stat.GlobalDefencesPlusPct),
        F(Stat.SpellSuppressionChancePctPerFrenzyCharge, Stat.BaseSpellSuppressionChancePct),
        F(Stat.ProjectileSpeedPlusPctPerFrenzyCharge, Stat.BaseProjectileSpeedPlusPct),
        F(Stat.LifeRegenerationRatePerMinutePctPerFrenzyCharge, Stat.LifeRegenerationRatePerMinutePct),
        F(Stat.AccuracyRatingPlusPctPerFrenzyCharge, Stat.AccuracyRatingPlusPct),
        F(Stat.AccuracyRatingPlusPerFrenzyCharge, Stat.AccuracyRating),
        F(Stat.CriticalStrikeChancePlusPctPerFrenzyCharge, Stat.CriticalStrikeChancePlusPct),
        F(Stat.BleedingDotMultiplierPerFrenzyChargePlus, Stat.BleedingDotMultiplierPlus),
        F(Stat.FasterBleedPerFrenzyChargePct, Stat.FasterBleedPct),
        F(Stat.PoisonDamagePlusPctPerFrenzyCharge, Stat.BasePoisonDamagePlusPct),
        F(Stat.PoisonDotMultiplierPlusPerFrenzyCharge, Stat.PoisonDotMultiplierPlus),
        F(Stat.LifeLeechFromAttackDamagePermyriadPerFrenzyCharge, Stat.BaseLifeLeechFromAttackDamagePermyriad),
        F(Stat.AdditionalAttackBlockPctPerFrenzyCharge, Stat.AdditionalBlockPct),
        F(Stat.PhysicalDamageReductionPercentPerFrenzyCharge, PhysicalReduction),
        F(Stat.LifeGainedOnEnemyDeathPerFrenzyCharge, Stat.BaseLifeGainedOnEnemyDeath),
        F(Stat.MinimumAddedColdDamagePerFrenzyCharge, Stat.AttackMinimumAddedColdDamage, Stat.SpellMinimumAddedColdDamage),
        F(Stat.MaximumAddedColdDamagePerFrenzyCharge, Stat.AttackMaximumAddedColdDamage, Stat.SpellMaximumAddedColdDamage),
        F(Stat.ColdDamagePctToAddAsChaosPerFrenzyCharge, Stat.ColdDamagePctToAddAsChaos),
        // Power
        P(Stat.CriticalStrikeChancePlusPctPerPowerCharge, Stat.CriticalStrikeChancePlusPct),
        P(Stat.AdditionalCriticalStrikeChancePerPowerChargePermyriad, Stat.AdditionalBaseCriticalStrikeChance),
        P(Stat.CriticalStrikeMultiplierPlusPerPowerCharge, Stat.BaseCriticalStrikeMultiplierPlus),
        P(Stat.CastSpeedPlusPctPerPowerCharge, Stat.BaseCastSpeedPlusPct),
        P(Stat.AttackAndCastSpeedPlusPctPerPowerCharge, Stat.AttackAndCastSpeedPlusPct),
        P(Stat.SkillAreaOfEffectPlusPctPerPowerCharge, Stat.BaseSkillAreaOfEffectPlusPct),
        new(Stat.SkillAreaOfEffectPlusPctPerPowerChargeUpTo50Pct, ChargeType.Power, [Stat.BaseSkillAreaOfEffectPlusPct], Cap: 50),
        P(Stat.SpellDamagePlusPctPerPowerCharge, Stat.SpellDamagePlusPct),
        P(Stat.ProjectileDamagePlusPctPerPowerCharge, Stat.ProjectileDamagePlusPct),
        P(Stat.DamagePlusPctPerPowerCharge, Stat.DamagePlusPct),
        P(Stat.ElementalDamagePlusPctPerPowerCharge, Stat.ElementalDamagePlusPct),
        P(Stat.ManaRegenerationRatePerMinutePctPerPowerCharge, Stat.ManaRegenerationRatePerMinutePct),
        P(Stat.ManaRegenerationRatePlusPctPerPowerCharge, Stat.ManaRegenerationRatePlusPct),
        P(Stat.ManaRegenerationRatePerMinutePerPowerCharge, Stat.BaseManaRegenerationRatePerMinute),
        P(Stat.LifeRegenerationRatePerMinutePctPerPowerCharge, Stat.LifeRegenerationRatePerMinutePct),
        P(Stat.AdditionalSpellBlockPctPerPowerCharge, Stat.BaseSpellBlockPct),
        P(Stat.AdditionalAttackBlockPctPerPowerCharge, Stat.AdditionalBlockPct),
        P(Stat.SpellSuppressionChancePctPerPowerCharge, Stat.BaseSpellSuppressionChancePct),
        P(Stat.ChanceToPoisonOnHitPctPerPowerCharge, Stat.BaseChanceToPoisonOnHitPct),
        P(Stat.PoisonDamagePlusPctPerPowerCharge, Stat.BasePoisonDamagePlusPct),
        P(Stat.PoisonDurationPlusPctPerPowerCharge, Stat.PoisonDurationPlusPct),
        P(Stat.ManaLeechFromAttackDamagePermyriadPerPowerCharge, Stat.BaseManaLeechFromAttackDamagePermyriad),
        P(Stat.LifeAndEnergyShieldRecoveryRatePlusPctPerPowerCharge,
            Stat.LifeRecoveryRatePlusPct, Stat.EnergyShieldRecoveryRatePlusPct),
        P(Stat.DamageTakenGoesToManaPctPerPowerCharge, Stat.BaseDamageRemovedFromManaBeforeLifePct),
        P(Stat.EnergyShieldPlusPctPerPowerCharge, Stat.MaximumEnergyShieldPlusPct),
        P(Stat.MovementSpeedPlusPctPerPowerCharge, Stat.BaseMovementVelocityPlusPct),
        P(Stat.PhysicalDamageReductionPercentPerPowerCharge, PhysicalReduction),
        P(Stat.CooldownRecoveryPlusPctPerPowerCharge, Stat.BaseCooldownSpeedPlusPct),
        P(Stat.MinimumAddedLightningDamagePerPowerCharge, Stat.AttackMinimumAddedLightningDamage, Stat.SpellMinimumAddedLightningDamage),
        P(Stat.MaximumAddedLightningDamagePerPowerCharge, Stat.AttackMaximumAddedLightningDamage, Stat.SpellMaximumAddedLightningDamage),
        P(Stat.MinimumAddedLightningDamageToSpellsPerPowerCharge, Stat.SpellMinimumAddedLightningDamage),
        P(Stat.MaximumAddedLightningDamageToSpellsPerPowerCharge, Stat.SpellMaximumAddedLightningDamage),
        P(Stat.SpellMinimumAddedColdDamagePerPowerCharge, Stat.SpellMinimumAddedColdDamage),
        P(Stat.SpellMaximumAddedColdDamagePerPowerCharge, Stat.SpellMaximumAddedColdDamage),
        P(Stat.LightningDamagePctToAddAsChaosPerPowerCharge, Stat.LightningDamagePctToAddAsChaos),
        // All three
        new(Stat.DamagePlusPctPerFrenzyPowerOrEnduranceCharge, null, [Stat.DamagePlusPct]),
        // Brutal (Endurance Charges converted): Character.ot's 3% chance to deal triple damage (the hits' hitRolls roll)
        // and 10% increased stun threshold per charge (PanelStatDefaults). Affliction's more ailment damage is
        // DamageMore(ailment); its more effect of non-damaging ailments and Absorption's recoup of elemental damage
        // taken as energy shield have no server consumer yet (11-skills.md §15).
        new(Stat.ChanceToDealTripleDamagePctPerBrutalCharge, ChargeType.Endurance, [Stat.BaseChanceToDealTripleDamagePct], Special: true),
        new(Stat.StunThresholdPlusPctPerBrutalCharge, ChargeType.Endurance, [Stat.StunThresholdPlusPct], Special: true),
    ];

    static readonly HashSet<uint> Expanded = [.. PerCharge.Select(r => r.Stat)];

    /// <summary>Whether a per-charge stat is one <see cref="Expand"/> turns into a stat the server calculates with (so a
    /// consumer gating on unsupported stats must not refuse it: PlayerHitRecovery's stun threshold and recovery).</summary>
    public static bool Expands(uint stat) => Expanded.Contains(stat);

    /// <summary>Stats that apply while the character has none of a type, or at least one.</summary>
    public sealed record Condition(uint Stat, ChargeType Type, bool WhenNone, uint[] Targets);

    /// <summary>The "with no … charges" / "while you have any … charges" stats whose effect the server models.</summary>
    public static readonly IReadOnlyList<Condition> Conditional =
    [
        new(Stat.DamageWhileNoFrenzyChargesPlusPct, ChargeType.Frenzy, true, [Stat.DamagePlusPct]),
        new(Stat.GlobalCriticalStrikeMultiplierPlusWhileYouHaveNoFrenzyCharges, ChargeType.Frenzy, true, [Stat.BaseCriticalStrikeMultiplierPlus]),
        new(Stat.SkillAreaOfEffectPlusPctWhileNoFrenzyCharges, ChargeType.Frenzy, true, [Stat.BaseSkillAreaOfEffectPlusPct]),
        new(Stat.CriticalStrikeMultiplierPlusWhileHaveAnyFrenzyCharges, ChargeType.Frenzy, false, [Stat.BaseCriticalStrikeMultiplierPlus]),
        new(Stat.AdditionalMaximumAllResistancesPctWithNoEnduranceCharges, ChargeType.Endurance, true, [Stat.AdditionalMaximumAllResistancesPct]),
    ];

    /// <summary>
    /// What the charges held add to the character's stats (<paramref name="get"/>: passives, items and auras with
    /// <see cref="Inherent"/>; <paramref name="held"/>: the effective count per type, Charges.State.Effective), by
    /// Stats.dat Id: <see cref="PerCharge"/>, <see cref="Conditional"/> and the virtual reductions
    /// <c>additional_physical_damage_reduction_%</c> (the passives' <c>base_additional_physical_damage_reduction_%</c>,
    /// which the charges add to) and <c>additional_elemental_damage_reduction_%</c>.
    /// <paramref name="converted"/>: the types whose charges are their special type's now (<see cref="Charges.Converted"/>;
    /// null: none). A converted type's charges count for its special rules only: the standard per-charge and "no
    /// charges" stats see none of them (pinned PoB CalcPerform zeroes the standard count). <paramref name="rules"/>: a
    /// caller's own rules after these; <paramref name="maximum"/>: the maxima its <see cref="Rule.PerMaximum"/> rules
    /// count (none without it).
    /// </summary>
    public static Dictionary<uint, int> Expand(Func<uint, int> get, Func<ChargeType, int> held, Func<ChargeType, bool>? converted = null,
        IEnumerable<Rule>? rules = null, Func<ChargeType, int>? maximum = null)
    {
        var added = new Dictionary<uint, int>();
        void Add(uint stat, long value)
        {
            if (value != 0) added[stat] = (int)Math.Clamp(added.GetValueOrDefault(stat) + value, int.MinValue, int.MaxValue);
        }
        int Standard(ChargeType type) => converted?.Invoke(type) == true ? 0 : held(type);
        int Special(ChargeType type) => converted?.Invoke(type) == true ? held(type) : 0;
        int total = Charges.Types.Sum(Standard);
        foreach (var rule in rules is null ? PerCharge : PerCharge.Concat(rules))
        {
            int perCharge = get(rule.Stat);
            if (perCharge == 0) continue;
            long value = (long)perCharge * (rule.PerMaximum ? rule.Type is { } m ? maximum?.Invoke(m) ?? 0 : 0
                : rule.Type is { } t ? rule.Special ? Special(t) : Standard(t) : total);
            if (rule.Cap is int cap) value = Math.Min(value, cap);
            foreach (uint target in rule.Targets) Add(target, value);
        }
        foreach (var c in Conditional)
            if (get(c.Stat) is int value and not 0 && (Standard(c.Type) == 0) == c.WhenNone)
                foreach (uint target in c.Targets) Add(target, value);
        Add(AdditionalPhysicalReduction, (long)get(PhysicalReduction) + added.GetValueOrDefault(PhysicalReduction));
        return added;
    }

    /// <summary>The panel stats of the counts (<paramref name="held"/>: effective, <paramref name="limits"/>: per type):
    /// <c>current_*_charges</c> (the Charges tab's "x / y" rows), <c>actual_current_*_charges</c> (BuffDefinitions
    /// <c>Current_StatsKey</c> of the charge buffs), <c>minimum_*_charges</c> and
    /// <c>current_removable_*_charges</c> (those above the minimum, which charge-consuming skills may spend).
    /// A converted type (<paramref name="converted"/>, <see cref="Charges.Converted"/>) shows its count on the special
    /// type's rows instead: <c>current_brutal_charges</c> (the Charges tab's row and the buff's <c>Current_StatsKey</c>)
    /// and <c>minimum_brutal_charges</c> (when <paramref name="minimumConverted"/>, the "modifiers to minimum … instead
    /// apply" flag), and zero on its own. The special rows are always sent, so a removed flag clears them.</summary>
    public static IEnumerable<(uint Stat, int Value)> Display(Func<ChargeType, int> held, Func<ChargeType, Charges.Limits> limits,
        Func<ChargeType, bool>? converted = null, Func<ChargeType, bool>? minimumConverted = null)
    {
        foreach (var type in Charges.Types)
        {
            var names = DisplayNames[(int)type];
            bool isConverted = converted?.Invoke(type) == true;
            int count = isConverted ? 0 : held(type), minimum = limits(type).Minimum;
            yield return (names.Current, count);
            yield return (names.ActualCurrent, count);
            yield return (names.Minimum, minimum);
            yield return (names.CurrentRemovable, Math.Max(0, count - minimum));
            yield return (names.CurrentSpecial, isConverted ? held(type) : 0);
            yield return (names.MinimumSpecial, isConverted && minimumConverted?.Invoke(type) == true ? minimum : 0);
        }
    }

    // Display's stat names per type (Charges.Types order), built once: every stat snapshot lists them.
    static readonly (uint Current, uint ActualCurrent, uint Minimum, uint CurrentRemovable, uint CurrentSpecial,
        uint MinimumSpecial)[] DisplayNames = [.. Charges.Types.Select(type =>
        {
            string name = Charges.Name(type), special = Charges.SpecialName(type);
            return (Stat.Of($"current_{name}_charges"), Stat.Of($"actual_current_{name}_charges"), Stat.Of($"minimum_{name}_charges"),
                Stat.Of($"current_removable_{name}_charges"), Stat.Of($"current_{special}_charges"), Stat.Of($"minimum_{special}_charges"));
        })];

    /// <summary>
    /// The more multiplier of the charges on a hit or on damage over time: Character.ot's 4% more damage per Frenzy
    /// Charge (<c>object_inherent_damage_+%_final_per_frenzy_charge</c> × <c>current_frenzy_charges</c>) and, for
    /// spells, <c>object_inherent_spell_damage_+%_final_per_power_charge</c> and the Quiet Might keystone's
    /// <c>quiet_might_keystone_spell_damage_+%_final_per_power_charge</c> × <c>current_power_charges</c> (the two Charges
    /// tab rows "Spell damage modifier per Power Charge"; each its own multiplier). A more modifier per charge is one
    /// multiplier of value × charges (Path of Building's Multiplier tag), not compounded. Damage with ailments
    /// (<paramref name="ailment"/>) takes Affliction Charges' <c>ailment_damage_+%_final_per_affliction_charge</c> ×
    /// <c>current_affliction_charges</c> too (the Charges tab's "Ailment damage per Affliction Charge" row; PoB
    /// CalcPerform's Damage MORE with KeywordFlag.Ailment).
    /// <paramref name="get"/> reads the character's final stats, which hold both the per-charge values and the counts.
    /// </summary>
    public static double DamageMore(Func<uint, int> get, bool spell = false, bool ailment = false)
    {
        static double More(long percent) => Math.Max(0, 100 + percent) / 100.0;
        double more = More((long)get(Stat.ObjectInherentDamagePlusPctFinalPerFrenzyCharge) * get(Stat.CurrentFrenzyCharges));
        if (spell)
        {
            int power = get(Stat.CurrentPowerCharges);
            more *= More((long)get(Stat.ObjectInherentSpellDamagePlusPctFinalPerPowerCharge) * power)
                * More((long)get(Stat.QuietMightKeystoneSpellDamagePlusPctFinalPerPowerCharge) * power);
        }
        if (ailment)
            more *= More((long)get(Stat.AilmentDamagePlusPctFinalPerAfflictionCharge) * get(Stat.CurrentAfflictionCharges));
        return more;
    }

    /// <summary>
    /// The additional damage reduction of a character against hits and damage over time, capped at
    /// <c>maximum_physical_damage_reduction_%</c> (Character.ot: 90, Path of Building's DamageReductionCap): physical
    /// from Endurance Charges and the passives' base stat, elemental (fire, cold, lightning) from Endurance Charges.
    /// Path of Building's CalcDefence adds the physical one to armour's reduction and applies the elemental one after
    /// resistance (DamageModel.MitigatePhysical, DamageModel.Reduce).
    /// </summary>
    public static (int Physical, int Elemental) Reduction(Func<uint, int> get) =>
        (Math.Clamp(get(PhysicalReduction), 0, GameServer.Core.World.DamageModel.DamageReductionCap),
         Math.Clamp(get(AdditionalElementalReduction), 0, GameServer.Core.World.DamageModel.DamageReductionCap));
}
