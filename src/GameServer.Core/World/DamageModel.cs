namespace GameServer.Core.World;

/// <summary>The damage types the server can mitigate, numbered in the game's conversion order:
/// physical, lightning, cold, fire, chaos.</summary>
public enum DamageType { Physical, Lightning, Cold, Fire, Chaos }

/// <summary>
/// Path of Exile's resistance rule for the simple cases. The floor and absolute cap are taken from Path of Building
/// (Tools/PathOfBuilding, commit 16de4b8): <c>ResistFloor = -200</c> and <c>MaxResistCap = 90</c> in
/// <c>src/Modules/Data.lua</c>; the default per-type maximum is 75 (<c>src/Data/Misc.lua</c>, Character.ot
/// <c>base_maximum_all_resistances_%</c>), and the final value is
/// <c>clamp(base × increased, ResistFloor, max)</c> in <c>src/Modules/CalcDefence.lua</c> (§ resistances).
/// Spell Suppression's 100% chance cap and 40% effect are from the same PoB data (<c>SuppressionChanceCap</c>,
/// <c>SuppressionEffect</c>); a suppressed spell hit takes 40% less damage.
/// The per-type maximum-resistance stats and increased resistance are not modelled yet; penetration subtracts from
/// the capped resistance (see <see cref="MitigateBy"/> and the per-type <see cref="Mitigate(DamagePacket, Func{DamageType, ResistanceValue})"/>).
/// </summary>
public static class DamageModel
{
    /// <summary>Path of Building's <c>data.misc.ResistFloor</c>.</summary>
    public const int ResistFloor = -200;

    /// <summary>Path of Building's <c>data.misc.MaxResistCap</c>: no resistance can exceed this even with max-res mods.</summary>
    public const int MaxResistCap = 90;

    /// <summary>Character.ot's <c>base_maximum_all_resistances_%</c>: the default maximum resistance.</summary>
    public const int DefaultMaxResistance = 75;

    /// <summary>Path of Building's <c>SuppressionChanceCap</c>: Spell Suppression chance cannot exceed 100%.</summary>
    public const int SpellSuppressionChanceCap = 100;

    /// <summary>Path of Building's <c>SuppressionEffect</c>: a suppressed spell hit takes 40% less damage.</summary>
    public const int SpellSuppressionEffect = 40;

    /// <summary>PoE's base critical strike multiplier: 150%, before <c>base_critical_strike_multiplier_+</c>.</summary>
    public const int BaseCriticalStrikeMultiplier = 150;

    /// <summary>Fireball's GrantEffectStatSetsPerLevel SpellCritChance (500 = 5.00%).</summary>
    public const int BaseSpellCriticalStrikeChancePerMyriad = 500;

    /// <summary>A hit's integer permyriad critical chance. Nonnegative midpoints round half up, shared by
    /// attack and spell rolls and panels. Keep the complete signed source sum until this final calculation.</summary>
    public static int CriticalStrikeChancePerMyriad(int basePerMyriad, long increasedPct) =>
        (int)decimal.Round(Math.Clamp(basePerMyriad * (100m + increasedPct) / 100m, 0m, 10_000m),
            0, MidpointRounding.AwayFromZero);

    /// <summary>The same critical chance expressed as a percentage for diagnostics and chance-based consumers.</summary>
    public static double CriticalStrikeChance(int basePerMyriad, long increasedPct) =>
        CriticalStrikeChancePerMyriad(basePerMyriad, increasedPct) / 100.0;

    /// <summary>A critical hit's damage, rounded to the nearest point; <paramref name="multiplier"/> is the final
    /// percentage (150 by default).</summary>
    public static int CriticalDamage(int damage, int multiplier)
    {
        if (damage <= 0) return 0;
        return (int)Math.Max(0, Math.Round(damage * Math.Max(0, multiplier) / 100.0));
    }

    /// <summary>Whether a Spell Suppression roll succeeds; <paramref name="roll"/> is 0..99. Lucky and unlucky roll
    /// twice: lucky keeps the better roll (1 − (1 − p)²), unlucky the worse (p²), as PoB's suppressRolls.</summary>
    public static bool Suppresses(int chance, int roll, bool lucky = false, bool unlucky = false) =>
        roll < EffectiveSuppressionChance(chance, lucky, unlucky);

    /// <summary>The chance after a lucky or unlucky suppression roll, capped by <see cref="SpellSuppressionChanceCap"/>.</summary>
    public static double EffectiveSuppressionChance(int chance, bool lucky = false, bool unlucky = false)
    {
        double p = Math.Clamp(chance, 0, SpellSuppressionChanceCap) / 100.0;
        if (lucky) p = 1 - (1 - p) * (1 - p);
        else if (unlucky) p *= p;
        return p * 100;
    }

    /// <summary>A successful suppression's damage after the suppression effect, rounded to the nearest point. PoB does
    /// not cap the effect itself (passives can raise it above 40); damage cannot go below zero.</summary>
    public static int Suppress(int damage, int effect = SpellSuppressionEffect)
    {
        if (damage <= 0) return 0;
        return (int)Math.Max(0, Math.Round(damage * (100.0 - Math.Max(0, effect)) / 100.0));
    }

    /// <summary>The resistance used for a hit: capped at the per-type maximum (75 by default, 90 absolute) and floored
    /// at Path of Building's -200.</summary>
    public static int Capped(int resistance, int maxResistance = DefaultMaxResistance) =>
        Math.Clamp(resistance, ResistFloor, Math.Min(maxResistance, MaxResistCap));

    /// <summary>Path of Building's <c>ResistOverCap</c> (CalcDefence.lua):
    /// <c>output[type.."ResistOverCap"] = m_max(0, total - max)</c>, where <c>total</c> is the uncapped resistance
    /// and <c>max</c> the per-type maximum capped at <see cref="MaxResistCap"/>. The overcapped part is what the
    /// "increased by overcapped resistance" modifiers read (CalcDefence's Evasion / CalcOffence's CritChance).</summary>
    public static int Overcap(int resistance, int maxResistance) =>
        Math.Max(0, resistance - Math.Min(maxResistance, MaxResistCap));

    /// <summary>The damage left after a resistance, rounded to the nearest point.</summary>
    public static int Mitigate(int damage, int resistance, int maxResistance = DefaultMaxResistance)
    {
        if (damage <= 0) return 0;
        return (int)Math.Max(0, Math.Round(damage * (100.0 - Capped(resistance, maxResistance)) / 100.0));
    }

    /// <summary>The damage left after a resistance, for damage over time where fractions are carried between ticks.</summary>
    public static double Mitigate(double damage, int resistance, int maxResistance = DefaultMaxResistance)
    {
        if (damage <= 0) return 0;
        return damage * (100.0 - Capped(resistance, maxResistance)) / 100.0;
    }

    /// <summary>The resistance, maximum resistance and penetration for one damage type: penetration subtracts from
    /// the capped resistance with no second clamp, so it can take the effective value below the floor.</summary>
    public readonly record struct ResistanceValue(int Value, int Maximum = DefaultMaxResistance, int Penetration = 0);

    /// <summary>Mitigate every damage type in a hit separately. The game applies each type's own resistance and
    /// maximum resistance; the hit's penetration subtracts from the capped value with no second clamp (Path of
    /// Building's CalcOffence 16de4b8: calcResistForType caps the resistance first, the hit branch then uses
    /// resist − pen), so penetration can take the effective resistance below the floor. The per-type components stay
    /// fractional; the caller rounds the final total.</summary>
    public static DamagePacket Mitigate(DamagePacket damage, Func<DamageType, ResistanceValue> resistance) =>
        damage.Select((type, range) =>
        {
            var r = resistance(type);
            int effective = Capped(r.Value, r.Maximum) - r.Penetration;
            return new DamageRange(MitigateBy(range.Min, effective), MitigateBy(range.Max, effective));
        });

    /// <summary>The share of the damage an already-effective resistance leaves (capped, with the penetration
    /// applied), kept fractional and never below zero; a below-floor effective resistance deals more than the raw
    /// damage. The caller rounds the final total once, not the components.</summary>
    public static double MitigateBy(double damage, int effectiveResistance)
    {
        if (damage <= 0) return 0;
        return Math.Max(0, damage * (100.0 - effectiveResistance) / 100.0);
    }

    /// <summary>Path of Building's <c>BlockChanceCap</c>: block chance cannot exceed 90%.</summary>
    public const int BlockChanceCap = 90;

    /// <summary>Character.ot's <c>maximum_block_%</c> and <c>base_maximum_spell_block_%</c>.</summary>
    public const int DefaultMaxBlockChance = 75, DefaultMaxSpellBlockChance = 75;

    /// <summary>Whether a block roll succeeds; <paramref name="roll"/> is 0..100. Lucky block rolls twice and keeps the
    /// better roll, so its effective chance is <c>1 - (1 - p)^2</c> (PoB CalcDefence's lucky block branch).</summary>
    public static bool Blocks(int chance, double roll, bool lucky = false, bool unlucky = false) =>
        roll < EffectiveBlockChance(chance, lucky, unlucky);

    /// <summary>The chance after lucky/unlucky block (one extra roll each); capped by <see cref="BlockChanceCap"/>.</summary>
    public static double EffectiveBlockChance(int chance, bool lucky = false, bool unlucky = false)
    {
        double p = Math.Clamp(chance, 0, BlockChanceCap) / 100.0;
        return lucky == unlucky ? p * 100 : lucky ? (1 - (1 - p) * (1 - p)) * 100 : p * p * 100;
    }

    /// <summary>
    /// Path of Building's <c>calcs.hitChance</c> (<c>src/Modules/CalcDefence.lua</c>): an attack's chance to hit an
    /// enemy with this evasion rating, as a whole percentage. The result is floored at 5% and capped at 100%.
    /// </summary>
    public static int AccuracyHitChance(int evasion, int accuracy)
    {
        if (accuracy <= 0) return 5;
        double raw = accuracy / (accuracy + Math.Pow(Math.Max(0, evasion) / 5.0, 0.9)) * 125;
        return Math.Clamp((int)Math.Floor(raw + 0.5), 5, 100);
    }

    /// <summary>Whether an attack hits; <paramref name="roll"/> is 0..99, and the chance is at least 5%.</summary>
    public static bool Hits(int chance, int roll) => roll < Math.Clamp(chance, 5, 100);

    /// <summary>Path of Building's <c>data.misc.DamageReductionCap</c>: physical damage reduction from armour is
    /// capped at 90%.</summary>
    public const int DamageReductionCap = 90;

    /// <summary>
    /// The physical damage reduction an armour rating gives against one hit, as a whole percentage. This is PoB's
    /// <c>calcs.armourReduction</c> (<c>armour / (armour + damage × 5) × 100</c>), rounded and capped at
    /// <see cref="DamageReductionCap"/>.
    /// </summary>
    public static int ArmourReduction(int armour, int damage)
    {
        if (armour <= 0 || damage <= 0) return 0;
        double reduction = armour / (armour + damage * 5.0) * 100;
        return Math.Clamp((int)Math.Floor(reduction + 0.5), 0, DamageReductionCap);
    }

    /// <summary>The physical hit left after armour, rounded to the nearest point.</summary>
    public static int MitigatePhysical(int damage, int armour) => MitigatePhysical(damage, armour, 0);

    /// <summary>
    /// The physical hit left after armour and additional physical damage reduction (Endurance Charges,
    /// <c>base_additional_physical_damage_reduction_%</c>), rounded to the nearest point. Path of Building's CalcDefence
    /// adds the two reductions and caps the sum at <see cref="DamageReductionCap"/>.
    /// </summary>
    public static int MitigatePhysical(int damage, int armour, int additionalReduction)
    {
        if (damage <= 0) return 0;
        int reduction = Math.Clamp(ArmourReduction(armour, damage) + Math.Max(0, additionalReduction), 0, DamageReductionCap);
        return (int)Math.Max(0, Math.Round(damage * (100.0 - reduction) / 100.0));
    }

    /// <summary>Damage after an additional damage reduction (elemental damage reduction from Endurance Charges, applied
    /// after resistance as Path of Building's CalcDefence does), capped at <see cref="DamageReductionCap"/>, rounded to
    /// the nearest point.</summary>
    public static int Reduce(int damage, int reduction)
    {
        if (damage <= 0) return 0;
        return (int)Math.Max(0, Math.Round(damage * (100.0 - Math.Clamp(reduction, 0, DamageReductionCap)) / 100.0));
    }

    /// <summary>The chance for an attack to evade a defender with this evasion rating, given the attacker's accuracy.
    /// The 5% minimum hit chance means evasion cannot exceed 95%.</summary>
    public static int EvadeChance(int evasion, int accuracy) => 100 - AccuracyHitChance(evasion, accuracy);
}
