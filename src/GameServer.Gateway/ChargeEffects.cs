// Standard-charge foundation adapted from PR #241 (85fffc1154797a97b0a09a939257abad98d791cc).
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
/// <c>virtual_maximum_*_charges</c> (<see cref="Display"/>).
/// </summary>
static class ChargeEffects
{
    /// <summary>Raw inputs whose developer replacement can change current limits or derived pool effects.</summary>
    public static bool IsInput(string name) => Charges.LimitInputs.Contains(name)
        || Inherent.Any(stat => stat.Stat == name) || PerCharge.Any(rule => rule.Stat == name);

    /// <summary>Metadata/Characters/Character.ot's per-charge stats (the Brine charge ones belong to another charge
    /// type and are left out). They are the base forms the panel rows sum and <see cref="PerCharge"/> reads.</summary>
    public static readonly IReadOnlyList<(string Stat, int Value)> Inherent =
    [
        ("base_attack_speed_+%_per_frenzy_charge", 4),
        ("base_cast_speed_+%_per_frenzy_charge", 4),
        ("object_inherent_damage_+%_final_per_frenzy_charge", 4),
        ("physical_damage_reduction_%_per_endurance_charge", 4),
        ("elemental_damage_reduction_%_per_endurance_charge", 4),
        ("critical_strike_chance_+%_per_power_charge", 50),
    ];

    /// <summary>One per-charge stat: × the charges of <paramref name="Type"/> held (null: all three types together), it
    /// adds to each of <paramref name="Targets"/>. <paramref name="Cap"/> limits the total ("up to 50%").</summary>
    public sealed record Rule(string Stat, ChargeType? Type, string[] Targets, int? Cap = null);

    // The physical one goes into the passives' base form, which the virtual total and the hits read; the elemental one has
    // no base form.
    const string PhysicalReduction = "base_additional_physical_damage_reduction_%";
    const string AdditionalPhysicalReduction = "additional_physical_damage_reduction_%";
    const string AdditionalElementalReduction = "additional_elemental_damage_reduction_%";

    static Rule E(string stat, params string[] targets) => new(stat, ChargeType.Endurance, targets);
    static Rule F(string stat, params string[] targets) => new(stat, ChargeType.Frenzy, targets);
    static Rule P(string stat, params string[] targets) => new(stat, ChargeType.Power, targets);

    /// <summary>
    /// The per-charge stats the server applies, and the stats they add to. "Adds X to Y damage per charge" without "to
    /// attacks/spells" is global added damage: it adds to both (Path of Building maps it without a skill flag).
    /// Added damage has a minimum and a maximum stat, each its own rule. Targets the server does not calculate with
    /// (ailments, leech, damage taken, wand-only or attack-only damage, mana cost, buff duration) are left out and
    /// listed in 11-skills.md §15.
    /// </summary>
    public static readonly IReadOnlyList<Rule> PerCharge =
    [
        // Endurance
        E("physical_damage_reduction_%_per_endurance_charge", PhysicalReduction),
        E("elemental_damage_reduction_%_per_endurance_charge", AdditionalElementalReduction),
        E("resist_all_elements_%_per_endurance_charge", "base_resist_all_elements_%"),
        E("chaos_damage_resistance_%_per_endurance_charge", "base_chaos_damage_resistance_%"),
        E("life_regeneration_rate_per_minute_%_per_endurance_charge", "life_regeneration_rate_per_minute_%"),
        E("life_regen_per_minute_per_endurance_charge", "base_life_regeneration_rate_per_minute"),
        E("melee_damage_+%_per_endurance_charge", "melee_damage_+%"),
        E("physical_damage_per_endurance_charge_+%", "physical_damage_+%"),
        E("damage_+%_per_endurance_charge", "damage_+%"),
        E("additional_block_%_per_endurance_charge", "additional_block_%"),
        E("additional_attack_block_%_per_endurance_charge", "additional_block_%"),
        E("physical_damage_reduction_rating_+%_per_endurance_charge", "physical_damage_reduction_rating_+%"),
        E("physical_damage_reduction_rating_per_endurance_charge", "base_physical_damage_reduction_rating"),
        E("spell_suppression_chance_%_per_endurance_charge", "base_spell_suppression_chance_%"),
        E("movement_speed_+%_per_endurance_charge", "base_movement_velocity_+%"),
        E("attack_and_cast_speed_+%_per_endurance_charge", "attack_and_cast_speed_+%"),
        E("critical_strike_chance_+%_per_endurance_charge", "critical_strike_chance_+%"),
        E("area_of_effect_+%_per_endurance_charge", "base_skill_area_of_effect_+%"),
        E("stun_duration_+%_per_endurance_charge", "base_stun_duration_+%"),
        E("minimum_added_fire_damage_per_endurance_charge", "attack_minimum_added_fire_damage", "spell_minimum_added_fire_damage"),
        E("maximum_added_fire_damage_per_endurance_charge", "attack_maximum_added_fire_damage", "spell_maximum_added_fire_damage"),
        E("minimum_added_physical_damage_per_endurance_charge", "attack_minimum_added_physical_damage", "spell_minimum_added_physical_damage"),
        E("maximum_added_physical_damage_per_endurance_charge", "attack_maximum_added_physical_damage", "spell_maximum_added_physical_damage"),
        E("fire_damage_%_to_add_as_chaos_per_endurance_charge", "fire_damage_%_to_add_as_chaos"),
        // Frenzy
        F("base_attack_speed_+%_per_frenzy_charge", "attack_speed_+%"),
        F("base_cast_speed_+%_per_frenzy_charge", "base_cast_speed_+%"),
        F("attack_and_cast_speed_+%_per_frenzy_charge", "attack_and_cast_speed_+%"),
        F("movement_velocity_+%_per_frenzy_charge", "base_movement_velocity_+%"),
        F("stun_recovery_+%_per_frenzy_charge", "base_stun_recovery_+%"),
        F("physical_damage_+%_per_frenzy_charge", "physical_damage_+%"),
        F("damage_+%_per_frenzy_charge", "damage_+%"),
        F("lightning_damage_+%_per_frenzy_charge", "lightning_damage_+%"),
        F("cold_damage_+%_per_frenzy_charge", "cold_damage_+%"),
        F("elemental_damage_+%_per_frenzy_charge", "elemental_damage_+%"),
        F("evasion_rating_+%_per_frenzy_charge", "evasion_rating_+%"),
        F("global_defences_+%_per_frenzy_charge", "global_defences_+%"),
        F("spell_suppression_chance_%_per_frenzy_charge", "base_spell_suppression_chance_%"),
        F("projectile_speed_+%_per_frenzy_charge", "base_projectile_speed_+%"),
        F("life_regeneration_rate_per_minute_%_per_frenzy_charge", "life_regeneration_rate_per_minute_%"),
        F("accuracy_rating_+%_per_frenzy_charge", "accuracy_rating_+%"),
        F("accuracy_rating_+_per_frenzy_charge", "accuracy_rating"),
        F("critical_strike_chance_+%_per_frenzy_charge", "critical_strike_chance_+%"),
        F("additional_attack_block_%_per_frenzy_charge", "additional_block_%"),
        F("physical_damage_reduction_percent_per_frenzy_charge", PhysicalReduction),
        F("life_gained_on_enemy_death_per_frenzy_charge", "base_life_gained_on_enemy_death"),
        F("minimum_added_cold_damage_per_frenzy_charge", "attack_minimum_added_cold_damage", "spell_minimum_added_cold_damage"),
        F("maximum_added_cold_damage_per_frenzy_charge", "attack_maximum_added_cold_damage", "spell_maximum_added_cold_damage"),
        F("cold_damage_%_to_add_as_chaos_per_frenzy_charge", "cold_damage_%_to_add_as_chaos"),
        // Power
        P("critical_strike_chance_+%_per_power_charge", "critical_strike_chance_+%"),
        P("critical_strike_multiplier_+_per_power_charge", "base_critical_strike_multiplier_+"),
        P("cast_speed_+%_per_power_charge", "base_cast_speed_+%"),
        P("attack_and_cast_speed_+%_per_power_charge", "attack_and_cast_speed_+%"),
        P("skill_area_of_effect_+%_per_power_charge", "base_skill_area_of_effect_+%"),
        new("skill_area_of_effect_+%_per_power_charge_up_to_50%", ChargeType.Power, ["base_skill_area_of_effect_+%"], Cap: 50),
        P("spell_damage_+%_per_power_charge", "spell_damage_+%"),
        P("projectile_damage_+%_per_power_charge", "projectile_damage_+%"),
        P("damage_+%_per_power_charge", "damage_+%"),
        P("elemental_damage_+%_per_power_charge", "elemental_damage_+%"),
        P("mana_regeneration_rate_per_minute_%_per_power_charge", "mana_regeneration_rate_per_minute_%"),
        P("mana_regeneration_rate_+%_per_power_charge", "mana_regeneration_rate_+%"),
        P("mana_regeneration_rate_per_minute_per_power_charge", "base_mana_regeneration_rate_per_minute"),
        P("life_regeneration_rate_per_minute_%_per_power_charge", "life_regeneration_rate_per_minute_%"),
        P("additional_spell_block_%_per_power_charge", "base_spell_block_%"),
        P("additional_attack_block_%_per_power_charge", "additional_block_%"),
        P("spell_suppression_chance_%_per_power_charge", "base_spell_suppression_chance_%"),
        P("energy_shield_+%_per_power_charge", "maximum_energy_shield_+%"),
        P("movement_speed_+%_per_power_charge", "base_movement_velocity_+%"),
        P("physical_damage_reduction_percent_per_power_charge", PhysicalReduction),
        P("cooldown_recovery_+%_per_power_charge", "base_cooldown_speed_+%"),
        P("minimum_added_lightning_damage_per_power_charge", "attack_minimum_added_lightning_damage", "spell_minimum_added_lightning_damage"),
        P("maximum_added_lightning_damage_per_power_charge", "attack_maximum_added_lightning_damage", "spell_maximum_added_lightning_damage"),
        P("minimum_added_lightning_damage_to_spells_per_power_charge", "spell_minimum_added_lightning_damage"),
        P("maximum_added_lightning_damage_to_spells_per_power_charge", "spell_maximum_added_lightning_damage"),
        P("spell_minimum_added_cold_damage_per_power_charge", "spell_minimum_added_cold_damage"),
        P("spell_maximum_added_cold_damage_per_power_charge", "spell_maximum_added_cold_damage"),
        P("lightning_damage_%_to_add_as_chaos_per_power_charge", "lightning_damage_%_to_add_as_chaos"),
        // All three
        new("damage_+%_per_frenzy_power_or_endurance_charge", null, ["damage_+%"]),
    ];

    /// <summary>Stats that apply while the character has none of a type, or at least one.</summary>
    public sealed record Condition(string Stat, ChargeType Type, bool WhenNone, string[] Targets);

    /// <summary>The "with no … charges" / "while you have any … charges" stats whose effect the server models.</summary>
    public static readonly IReadOnlyList<Condition> Conditional =
    [
        new("damage_while_no_frenzy_charges_+%", ChargeType.Frenzy, true, ["damage_+%"]),
        new("global_critical_strike_multiplier_+_while_you_have_no_frenzy_charges", ChargeType.Frenzy, true, ["base_critical_strike_multiplier_+"]),
        new("skill_area_of_effect_+%_while_no_frenzy_charges", ChargeType.Frenzy, true, ["base_skill_area_of_effect_+%"]),
        new("critical_strike_multiplier_+_while_have_any_frenzy_charges", ChargeType.Frenzy, false, ["base_critical_strike_multiplier_+"]),
        new("additional_maximum_all_resistances_%_with_no_endurance_charges", ChargeType.Endurance, true, ["additional_maximum_all_resistances_%"]),
    ];

    /// <summary>
    /// What the charges held add to the character's stats (<paramref name="get"/>: passives, items and auras with
    /// <see cref="Inherent"/>; <paramref name="held"/>: the effective count per type, Charges.State.Effective), by
    /// Stats.dat Id: <see cref="PerCharge"/>, <see cref="Conditional"/> and the virtual reductions
    /// <c>additional_physical_damage_reduction_%</c> (the passives' <c>base_additional_physical_damage_reduction_%</c>,
    /// which the charges add to) and <c>additional_elemental_damage_reduction_%</c>.
    /// </summary>
    public static Dictionary<string, int> Expand(Func<string, int> get, Func<ChargeType, int> held)
    {
        var added = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string stat, long value)
        {
            if (value != 0) added[stat] = (int)Math.Clamp(added.GetValueOrDefault(stat) + value, int.MinValue, int.MaxValue);
        }
        long total = Charges.Types.Sum(t => (long)held(t));
        foreach (var rule in PerCharge)
        {
            int perCharge = get(rule.Stat);
            if (perCharge == 0) continue;
            long value = (long)Math.Clamp((decimal)perCharge * (rule.Type is { } t ? held(t) : total), int.MinValue, int.MaxValue);
            if (rule.Cap is int cap) value = Math.Min(value, cap);
            foreach (string target in rule.Targets) Add(target, value);
        }
        foreach (var c in Conditional)
            if (get(c.Stat) is int value and not 0 && (held(c.Type) == 0) == c.WhenNone)
                foreach (string target in c.Targets) Add(target, value);
        Add(AdditionalPhysicalReduction, (long)get(PhysicalReduction) + added.GetValueOrDefault(PhysicalReduction));
        return added;
    }

    /// <summary>The panel stats of the counts (<paramref name="held"/>: effective, <paramref name="limits"/>: per type):
    /// <c>current_*_charges</c> (the Charges tab's "x / y" rows), <c>actual_current_*_charges</c> (BuffDefinitions
    /// <c>Current_StatsKey</c> of the charge buffs), <c>minimum_*_charges</c> and
    /// <c>current_removable_*_charges</c> (those above the minimum, which charge-consuming skills may spend).</summary>
    public static IEnumerable<(string Stat, int Value)> Display(Func<ChargeType, int> held, Func<ChargeType, Charges.Limits> limits)
    {
        foreach (var type in Charges.Types)
        {
            string name = Charges.Name(type);
            int count = held(type), minimum = limits(type).Minimum;
            yield return ($"current_{name}_charges", count);
            yield return ($"actual_current_{name}_charges", count);
            yield return ($"minimum_{name}_charges", minimum);
            yield return ($"current_removable_{name}_charges", Math.Max(0, count - minimum));
        }
    }

    /// <summary>
    /// The more multiplier of the charges on a hit or on damage over time: Character.ot's 4% more damage per Frenzy
    /// Charge (<c>object_inherent_damage_+%_final_per_frenzy_charge</c> × <c>current_frenzy_charges</c>) and, for
    /// spells, <c>object_inherent_spell_damage_+%_final_per_power_charge</c> and the Quiet Might keystone's
    /// <c>quiet_might_keystone_spell_damage_+%_final_per_power_charge</c> × <c>current_power_charges</c> (the two Charges
    /// tab rows "Spell damage modifier per Power Charge"; each its own multiplier). A more modifier per charge is one
    /// multiplier of value × charges (Path of Building's Multiplier tag), not compounded.
    /// <paramref name="get"/> reads the character's final stats, which hold both the per-charge values and the counts.
    /// </summary>
    public static double DamageMore(Func<string, int> get, bool spell = false)
    {
        static double More(long percent) => Math.Max(0, 100 + percent) / 100.0;
        double more = More((long)get("object_inherent_damage_+%_final_per_frenzy_charge") * get("current_frenzy_charges"));
        if (spell)
        {
            int power = get("current_power_charges");
            more *= More((long)get("object_inherent_spell_damage_+%_final_per_power_charge") * power)
                * More((long)get("quiet_might_keystone_spell_damage_+%_final_per_power_charge") * power);
        }
        return more;
    }

    /// <summary>
    /// The additional damage reduction of a character against hits and damage over time, capped at
    /// <c>maximum_physical_damage_reduction_%</c> (Character.ot: 90, Path of Building's DamageReductionCap): physical
    /// from Endurance Charges and the passives' base stat, elemental (fire, cold, lightning) from Endurance Charges.
    /// Path of Building's CalcDefence adds the physical one to armour's reduction and applies the elemental one after
    /// resistance (DamageModel.MitigatePhysical, DamageModel.Reduce).
    /// </summary>
    public static (int Physical, int Elemental) Reduction(Func<string, int> get) =>
        (Math.Clamp(get(PhysicalReduction), 0, GameServer.Core.World.DamageModel.DamageReductionCap),
         Math.Clamp(get(AdditionalElementalReduction), 0, GameServer.Core.World.DamageModel.DamageReductionCap));
}
