/// <summary>
/// Per-charge stats of passives and items beyond <see cref="ChargeEffects.PerCharge"/>, each into the stat the server
/// already calculates with (stat descriptions for the wording; the targets are the plain stats of the same wording):
/// Shimmeron's "+0.3% to Critical Strike Chance per Power Charge" (hundredths, as
/// <c>additional_base_critical_strike_chance</c>), Auxium's elemental attack damage, Machina Mitts' Life and Energy Shield
/// recovery rate, the Badges of the Brotherhood's Elusive effect, travel skill cooldown and Fortification, Maligaro's
/// Cruelty's and Fenumus' Toxins' poison, Usurper's Penance's faster bleeding, Hyaon's Fury's damage taken, The
/// Aylardex's "Damage is taken from Mana before Life per Power Charge", the "reduced Extra Damage from Critical
/// Strikes per Endurance Charge" implicit;
/// Assassin's Deathmarked "per maximum Power Charge" Mark effect and instant leech. <see cref="Conditions"/>: Ahn's
/// Contempt's "reduced Extra Damage from Critical Strikes while you have no Power Charges". The "while at maximum …
/// Charges" item stats are StatConditions' (ItemConditionFlags), not here.
/// </summary>
public static class ChargeScaling
{
    static ChargeEffects.Rule E(string stat, params string[] targets) => new(stat, ChargeType.Endurance, targets);
    static ChargeEffects.Rule F(string stat, params string[] targets) => new(stat, ChargeType.Frenzy, targets);
    static ChargeEffects.Rule P(string stat, params string[] targets) => new(stat, ChargeType.Power, targets);

    public static readonly IReadOnlyList<ChargeEffects.Rule> Rules =
    [
        P("additional_critical_strike_chance_per_power_charge_permyriad", "additional_base_critical_strike_chance"),
        P("elemental_damage_with_attack_skills_+%_per_power_charge", "elemental_damage_with_attack_skills_+%"),
        P("life_and_energy_shield_recovery_rate_+%_per_power_charge", "life_recovery_rate_+%", "energy_shield_recovery_rate_+%"),
        P("elusive_effect_on_self_+%_per_power_charge", "elusive_effect_+%"),
        P("poison_duration_+%_per_power_charge", "poison_duration_+%"),
        P("poison_damage_+%_per_power_charge", "base_poison_damage_+%"),
        P("chance_to_poison_on_hit_%_per_power_charge", "base_chance_to_poison_on_hit_%"),
        P("damage_taken_goes_to_mana_%_per_power_charge", "base_damage_removed_from_mana_before_life_%"),
        F("poison_damage_+%_per_frenzy_charge", "base_poison_damage_+%"),
        F("faster_bleed_per_frenzy_charge_%", "faster_bleed_%"),
        F("travel_skills_cooldown_speed_+%_per_frenzy_charge", "travel_skill_cooldown_speed_+%"),
        F("damage_taken_+%_per_frenzy_charge", "base_damage_taken_+%"),
        E("base_max_fortification_per_endurance_charge", "base_max_fortification"),
        E("self_critical_strike_multiplier_-%_per_endurance_charge", "base_self_critical_strike_multiplier_-%"),
        new("mark_skills_curse_effect_+%_per_maximum_power_charge", ChargeType.Power, ["mark_skills_curse_effect_+%"], PerMaximum: true),
        new("leech_%_is_instant_per_maximum_power_charge", ChargeType.Power, ["leech_%_is_instant"], PerMaximum: true),
    ];

    public static readonly IReadOnlyList<ChargeEffects.Condition> Conditions =
    [
        new("extra_damage_taken_from_crit_while_no_power_charges_+%", ChargeType.Power, true,
            ["base_self_critical_strike_multiplier_-%"], Scale: -1),
    ];

    /// <summary>"Count as having maximum number of … Charges" (Ralakesh's Impatience): the per-charge effects and the
    /// counts shown use the maximum.</summary>
    public static bool CountsAsMaximum(ChargeType type, Func<string, int> get) =>
        get($"count_as_having_max_{Charges.Name(type)}_charges") != 0 || get("count_as_having_max_endurance_frenzy_power_charges") != 0;
}
