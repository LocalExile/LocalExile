using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;


/// <summary>
/// Whether a stat's condition holds for a character right now (issue #504). Stats.dat carries the condition in the id
/// itself — <c>attack_speed_while_dual_wielding_+%</c>, <c>chance_to_evade_attacks_%_while_affected_by_grace</c> — and the
/// server previously ignored the suffix, so every such stat counted unconditionally.
/// <para>
/// This is the single place that decides. A caller builds a <see cref="Subject"/> from what it knows about the character
/// and passes it to the stat sum; <see cref="Of"/> then answers per stat id. When no subject is given every condition is
/// <see cref="State.NotModelled"/> and no stat changes, so a caller that cannot supply one keeps its old behaviour.
/// </para>
/// <para>
/// The scope is the conditions the server can already answer from state it keeps: the character's weapons and shield and
/// the auras it runs. A condition whose subject the server does not model yet (a shrine or
/// a league buff) is reported as <see cref="State.NotModelled"/> rather than as false, so those stats keep their previous
/// behaviour and can be listed instead of silently changing.
/// </para>
/// </summary>
public static class StatConditions
{
    /// <summary>What the server can say about a stat's condition.</summary>
    public enum State
    {
        /// <summary>The id carries no condition: always counted, as before.</summary>
        None,

        /// <summary>The condition holds for this character now.</summary>
        Holds,

        /// <summary>The condition does not hold for this character now.</summary>
        Fails,

        /// <summary>The condition's subject is not modelled yet: counted unconditionally, as before, and reported.</summary>
        NotModelled,
    }

    /// <summary>Whether a state means the stat's value counts.</summary>
    public static bool Counts(State state) => state is State.None or State.Holds or State.NotModelled;

    /// <summary>The weapon/shield condition of <see cref="Of"/>, as a suffix in the stat id.</summary>
    static readonly string[] WeaponSuffixes =
    [
        DualWield, WieldingShield, UsingShield, HoldingAShield, HoldingShield,
        TwoHanded + "_weapon", UsingTwoHanded + "_weapon", OneHanded + "_weapon", UsingOneHanded + "_weapon",
        TwoHanded, UsingTwoHanded, OneHanded, UsingOneHanded,
    ];

    /// <summary>
    /// The conditional ids that already have a consumer of their own, gating on the same state
    /// (<c>Progression</c>, <c>AccuracyRating</c>, <c>AttackBaseCriticalStrike</c>, <c>ChargeGains</c>,
    /// <c>ChargeEffects</c>). Folding one of these onto its plain name as well would count the same mod twice, so they
    /// are left as they are.
    /// <para>
    /// The <c>_per_&lt;type&gt;_charge</c> coefficients are listed by <see cref="NamesACharge"/> instead of by id:
    /// <c>ChargeEffects.PerCharge</c> names 75 of them and expands them against the charge counts already.
    /// </para>
    /// </summary>
    static readonly HashSet<string> DedicatedConsumers = new(StringComparer.Ordinal)
    {
        "accuracy_rating_while_dual_wielding_+%",
        "add_frenzy_charge_on_kill_%_chance_while_dual_wielding",
        "attack_speed_while_dual_wielding_+%",
        "block_while_dual_wielding_%",
        "block_while_dual_wielding_claws_%",
        "critical_strike_chance_while_dual_wielding_+%",
        "critical_strike_chance_while_wielding_shield_+%",
        "critical_strike_multiplier_while_dual_wielding_+",
        "spell_block_while_dual_wielding_%",
        // The "_while_holding_shield" spelling (the shield/jewel mods) has its own consumers too: the two block stats in
        // Progression's shield block, the shield attack speed in its attack-speed block, and the endurance charge on
        // kill in ChargeGains. Left on their own id so those readers still find them and do not double count.
        "attack_speed_+%_while_holding_shield",
        "block_chance_%_while_holding_shield",
        "block_spells_chance_%_while_holding_shield",
        "endurance_charge_on_kill_percent_chance_while_holding_shield",
        // Doppelgänger Guise consumes these exact conditional finals in its own Unhinge owner.
        "critical_strike_chance_+%_final_while_unhinged",
        "physical_and_chaos_damage_taken_+%_final_while_not_unhinged",
        "enemies_explode_on_kill_while_unhinged",
        // Her Embrace integrates its own qualified source's Fire degeneration over the accepted buff interval.
        "%_maximum_es_and_life_taken_as_fire_damage_per_minute_per_level_while_in_her_embrace",
        // Per-Rage item coefficients are multiplied by the authoritative effective Rage in RageStats, not folded.
        "bleed_dot_multiplier_+_per_rage_if_equipped_axe",
        "you_count_as_low_life_while_affected_by_vulnerability",
        "you_count_as_full_life_while_affected_by_vulnerability",
        "lose_power_charge_each_second_if_not_detonated_mines_recently",
        // The source-owned phasing producer gates these before publishing the pathing flag.
        "gain_phasing_while_you_have_low_life", "gain_phasing_if_enemy_killed_recently",
        "phasing_if_blocked_recently", "gain_phasing_while_you_have_onslaught",
        "gain_phasing_while_at_maximum_frenzy_charges", "gain_phasing_while_affected_by_haste",
    };

    static readonly string[] StateSuffixes =
    [
        "_while_on_full_energy_shield", "_while_es_full", "_on_full_energy_shield", "_while_no_energy_shield",
        "_while_on_full_life", "_while_on_low_life",
        "_while_not_low_life", "_while_not_on_low_life",
        "_while_on_full_mana", "_while_on_low_mana", "_while_not_on_low_mana",
        "_while_no_life_is_reserved", "_while_no_mana_reserved",
        "_while_moving", "_while_stationary", "_while_not_moving", "_while_phasing", "_while_you_have_phasing",
        "_while_not_ignited_frozen_shocked", "_while_ignited", "_while_chilled", "_while_frozen", "_while_shocked", "_while_fortified", "_while_you_have_fortify",
        "_while_unarmed", "_while_wielding_staff", "_while_wielding_sword",
        "_while_life_leeching", "_while_mana_leeching", "_while_energy_shield_leeching", "_while_es_leeching", "_while_leeching",
        "_while_at_maximum_endurance_charges", "_while_at_maximum_frenzy_charges", "_while_at_maximum_power_charges",
        "_while_using_flask", "_during_flask_effect", "_if_affected_by_no_flasks",
        "_while_bleeding", "_while_poisoned", "_while_on_consecrated_ground",
        "_while_cursed", "_while_not_cursed",
        .. new[] { "frenzy", "power", "endurance" }.SelectMany(type => new[]
        {
            $"_while_no_{type}_charges", $"_while_you_have_no_{type}_charges", $"_while_have_any_{type}_charges",
        }),
        "_while_onslaught_is_active", "_while_have_onslaught", "_while_channelling", "_while_not_channelling",
        "_while_you_have_active_physical_aegis", "_while_you_have_depleted_physical_aegis",
        "_while_unhinged", "_while_not_unhinged",
        "_while_you_have_avatar_of_fire", "_while_you_do_not_have_avatar_of_fire",
        "_while_affected_by_elusive",
        "_if_you_have_beast_minion",
        "_if_golem_summoned_in_past_8_seconds",
        "_while_in_blood_stance", "_in_blood_stance", "_while_in_sand_stance", "_in_sand_stance",
        "_if_changed_stance_recently", "_while_changed_stance_recently",
        "_if_you_have_cat_aspect", "_while_you_have_cat_aspect", "_while_have_cat_aspect",
        "_if_you_have_cats_stealth", "_while_you_have_cats_stealth", "_while_have_cats_stealth",
        "_while_you_have_cat_stealth",
        "_if_you_have_cats_agility", "_while_you_have_cats_agility", "_while_have_cats_agility",
        // The item mods spell the same life/mana states with "when" rather than "while" (Lioneye's Paws, Daresso's
        // Salute, Infernal Mantle); ConditionalItemPools publishes both keys for the same state.
        "_when_on_full_life", "_when_on_low_life", "_when_on_full_mana", "_when_on_low_mana",
    ];

    const string MagicRingCondition = "_if_magic_ring_equipped";
    const string MagicItemCondition = "_if_wearing_a_magic_item";
    const string NormalItemCondition = "_if_wearing_a_normal_item";
    const string EquippedAxeCondition = "_if_equipped_axe";
    static readonly string[] SlotConditionSuffixes =
        ["_while_off_hand_is_empty", "_while_off_hand_empty", MagicRingCondition, MagicItemCondition, NormalItemCondition];

    static readonly string[] TrailingUnits = ["_permyriad", "_%_chance", "_+%", "_%", "_+"];

    static bool EndsWithCondition(string id, string condition) => id.EndsWith(condition, StringComparison.Ordinal)
        || TrailingUnits.Any(unit => id.EndsWith(condition + unit, StringComparison.Ordinal));

    static string? StateSuffix(string statId) => StateSuffixes.FirstOrDefault(s => EndsWithCondition(statId, s));

    /// <summary>The plain stat a conditional id names: <c>spell_damage_+%_while_dual_wielding</c> →
    /// <c>spell_damage_+%</c>, and <c>damage_+%_if_you_have_frozen_enemy_recently</c> → <c>damage_+%</c>. Null when the
    /// id carries no condition this build can move.</summary>
    static System.Collections.Concurrent.ConcurrentDictionary<string, string?>? PlainNames;

    public static string? PlainOf(string statId) =>
        System.Threading.LazyInitializer.EnsureInitialized(ref PlainNames,
            static () => new(StringComparer.Ordinal)).GetOrAdd(statId, ParsePlainName);

    static string? ParsePlainName(string statId)
    {
        var property = EquipmentPropertyThresholdId.Match(statId);
        if (property.Success)
        {
            string prefix = statId[..property.Index];
            return PlainOf(prefix) ?? prefix;
        }
        var ring = RingConditionId.Match(statId);
        if (ring.Success)
        {
            string prefix = statId[..ring.Index];
            return PlainOf(prefix) ?? prefix;
        }
        var difference = ResourceDifferenceId.Match(statId);
        if (difference.Success)
        {
            string prefix = statId[..difference.Index];
            return PlainOf(prefix) ?? prefix;
        }
        var below = BelowThresholdId.Match(statId);
        if (below.Success)
        {
            string prefix = statId[..below.Index];
            return PlainOf(prefix) ?? prefix;
        }
        var equipment = EquipmentThresholdId.Match(statId);
        if (equipment.Success)
        {
            string prefix = statId[..equipment.Index];
            return PlainOf(prefix) ?? prefix;
        }
        var collection = EquipmentCollectionId.Match(statId);
        if (collection.Success)
        {
            string prefix = statId[..collection.Index];
            return PlainOf(prefix) ?? prefix;
        }
        foreach (var expression in new[] { AttributeComparisonId, EquipmentAmountId })
        {
            var match = expression.Match(statId);
            if (match.Success)
            {
                string prefix = statId[..match.Index];
                return PlainOf(prefix) ?? prefix;
            }
        }
        foreach (string suffix in WeaponSuffixes)
        {
            int at = statId.IndexOf(suffix, StringComparison.Ordinal);
            if (at < 0) continue;
            return Trim(statId, at, at + suffix.Length);
        }
        foreach (string suffix in SlotConditionSuffixes)
        {
            int at = statId.IndexOf(suffix, StringComparison.Ordinal);
            if (at < 0) continue;
            return Trim(statId, at, at + suffix.Length);
        }
        // "…_recently": the whole condition sits at the end, so everything before it is the plain stat.
        var recent = RecentId.Match(statId);
        if (recent.Success) return Trim(statId, recent.Index, recent.Index + recent.Length);
        var cast = RecentCastId.Match(statId);
        if (TryRecentCast(statId, out _, out _)) return Trim(statId, cast.Index, cast.Index + cast.Length);
        var eightSecond = RecentEightSecondsId.Match(statId);
        if (eightSecond.Success) return Trim(statId, eightSecond.Index, eightSecond.Index + eightSecond.Length);
        foreach (var expression in new[] { RecentCountThreshold, RecentAmountId })
        {
            var match = expression.Match(statId);
            if (match.Success) return Trim(statId, match.Index, match.Index + match.Length);
        }
        // "…_if_you_have_at_least_N_<attribute>".
        var threshold = ThresholdId.Match(statId);
        if (threshold.Success) return Trim(statId, threshold.Index, threshold.Index + threshold.Length);
        if (StateSuffix(statId) is { } stateSuffix)
        {
            int at = statId.LastIndexOf(stateSuffix, StringComparison.Ordinal);
            return Trim(statId, at, at + stateSuffix.Length);
        }
        foreach (string prefix in new[] { WhileAffectedBy, IfAffectedBy })
        {
            int at = statId.IndexOf(prefix, StringComparison.Ordinal);
            if (at >= 0 && TryAuraName(statId, out string family) && IsModelledAura(family))
                return Trim(statId, at, at + prefix.Length + family.Length);
        }
        return null;

        static string? Trim(string id, int from, int to)
        {
            string plain = (id[..from] + id[to..]).Trim('_');
            return plain.Length > 0 && !string.Equals(plain, id, StringComparison.Ordinal) ? plain : null;
        }
    }

    /// <summary>
    /// Where a conditional stat goes once its condition holds (issue #504): onto its plain name, which is what the
    /// pools, the panels and the damage pipeline already read. Without this the condition is decided correctly but the
    /// value lands on an id nothing consumes, so a conditional mod still does nothing.
    /// <para>
    /// Null when the id carries no condition this build moves, when it has a consumer of its own, or when the plain
    /// name is not a stat of this build — a caller then keeps the id as it is, exactly as before.
    /// </para>
    /// </summary>
    public static string? Fold(string statId, Func<string, bool> exists)
    {
        if (statId == "additional_critical_strike_chance_permyriad_while_affected_by_cat_aspect")
            return exists("additional_base_critical_strike_chance") ? "additional_base_critical_strike_chance" : null;
        if (DedicatedConsumers.Contains(statId) || ChargeEffects.Expands(statId)
            || IncomingAilmentEffects.Expands(statId)) return null;
        if (PlainOf(statId) is not { } plain) return null;
        // Table spellings shared by several conditional rolls, mapped to the existing unconditional consumer.
        plain = plain switch
        {
            "evasion_+%" => "evasion_rating_+%",
            "evasion_rating_plus" => "base_evasion_rating",
            "evasion_rating_+" => "base_evasion_rating",
            "armour_+%" => "physical_damage_reduction_rating_+%",
            "armour" or "armour_+" or "X_armour" => "base_physical_damage_reduction_rating",
            "local_chance_to_bleed_%" => "local_chance_to_bleed_on_hit_%",
            "physical_damage_reduction_rating_%" => "physical_damage_reduction_rating_+%",
            "maximum_es_+%" => "maximum_energy_shield_+%",
            "life_+%" => "maximum_life_+%",
            "movement_speed_+%" or "movement_velocity_+%" => "base_movement_velocity_+%",
            "life_regeneration_per_minute" => "base_life_regeneration_rate_per_minute",
            "life_regeneration_rate_per_minute" => "base_life_regeneration_rate_per_minute",
            "life_regeneration_per_minute_%" => "life_regeneration_rate_per_minute_%",
            "energy_shield_regeneration_%_per_minute" or "energy_shield_regeneration_rate_per_minute_%"
                => "base_energy_shield_regeneration_rate_per_minute_%",
            // The historical "recharge" ids on equipment collection conditions describe flat regeneration.
            "energy_shield_recharge_rate_per_minute" when EquipmentCollectionId.IsMatch(statId)
                => "base_energy_shield_regeneration_rate_per_minute",
            "mana_recharge_rate_per_minute" when EquipmentCollectionId.IsMatch(statId)
                => "base_mana_regeneration_rate_per_minute",
            "life_gain_per_target" => "base_life_gain_per_target",
            "damage_taken_+%" => "base_damage_taken_+%",
            "critical_strike_multiplier_+" or "global_critical_strike_multiplier_+" => "base_critical_strike_multiplier_+",
            "critical_strike_multiplier_+%" => "base_critical_strike_multiplier_+",
            "cooldown_recovery_+%" => "base_cooldown_speed_+%",
            "action_speed_cannot_be_slowed_below_base" => "action_speed_cannot_be_reduced_below_base",
            "take_no_extra_damage_from_critical_strikes" => "self_take_no_extra_damage_from_critical_strikes",
            "extra_damage_taken_from_crit_+%" => "base_self_critical_strike_multiplier_-%",
            "additional_critical_strike_chance_permyriad" => "additional_base_critical_strike_chance",
            "critical_strike_chance_+%_for_spells" => "spell_critical_strike_chance_+%",
            "critical_strike_multiplier_+_for_spells" => "spell_critical_strike_multiplier_+",
            "physical_damage_%_to_gain_as_chaos" => "physical_damage_%_to_add_as_chaos",
            "physical_damage_%_to_add_as_fire_damage" => "physical_damage_%_to_add_as_fire",
            "physical_damage_%_to_add_as_lightning_damage" => "physical_damage_%_to_add_as_lightning",
            "fire_penetration_%" => "base_reduce_enemy_fire_resistance_%",
            "elemental_penetration_%" => "base_penetrate_elemental_resistances_%",
            "block_%" => "additional_block_%",
            "chance_to_deal_triple_damage_%" => "base_chance_to_deal_triple_damage_%",
            "double_damage_chance_%" => "chance_to_deal_double_damage_%",
            "chance_to_deal_double_damage_+%" => "chance_to_deal_double_damage_%",
            "intimidate_enemies_on_hit" => "chance_to_intimidate_on_hit_%",
            "inflict_withered_for_2_seconds_on_hit" => "withered_on_hit_for_2_seconds_%_chance",
            "inflict_fire_exposure" => "base_inflict_fire_exposure_on_hit_%_chance",
            "inflict_cold_exposure" => "base_inflict_cold_exposure_on_hit_%_chance",
            "inflict_lightning_exposure" => "base_inflict_lightning_exposure_on_hit_%_chance",
            "burning_damage_+%" => "burn_damage_+%",
            "attacks_bleed_on_hit" => "global_bleed_on_hit",
            "gain_crimson_dance" => "keystone_crimson_dance",
            "gain_iron_reflexes" => "keystone_iron_reflexes",
            "you_have_zealots_oath" => "keystone_zealots_oath",
            "life_leech_from_fire_damage_permyriad" => "base_life_leech_from_fire_damage_permyriad",
            "life_leech_from_spell_damage_permyriad" => "base_life_leech_from_spell_damage_permyriad",
            "energy_shield_leech_from_lightning_damage_permyriad" => "base_energy_shield_leech_from_lightning_damage_permyriad",
            "unaffected_by_bleeding" or "unaffected_by_bleed" => "base_unaffected_by_bleeding",
            "unaffected_by_poison" => "base_unaffected_by_poison",
            "damage_removed_from_mana_before_life_%" => "base_damage_removed_from_mana_before_life_%",
            "physical_damage_reduction_%" => "base_additional_physical_damage_reduction_%",
            "minimum_added_cold_damage" => "global_minimum_added_cold_damage",
            "maximum_added_cold_damage" => "global_maximum_added_cold_damage",
            "minimum_added_fire_damage" => "global_minimum_added_fire_damage",
            "maximum_added_fire_damage" => "global_maximum_added_fire_damage",
            "spell_and_attack_minimum_added_chaos_damage" => "global_minimum_added_chaos_damage",
            "spell_and_attack_maximum_added_chaos_damage" => "global_maximum_added_chaos_damage",
            "number_of_additional_chains_for_projectiles" => "number_of_chains",
            "projectiles_pierce_x_additional_targets" => "projectile_number_of_targets_to_pierce",
            "avoid_projectiles_%_chance" => "base_avoid_projectiles_%_chance",
            "avoid_elemental_ailments_%" => "avoid_all_elemental_status_%",
            "area_of_effect_+%" => "base_skill_area_of_effect_+%",
            "chaos_resistance_+" => "base_chaos_damage_resistance_%",
            "fire_damage_resistance_%" => "base_fire_damage_resistance_%",
            "cold_damage_resistance_%" => "base_cold_damage_resistance_%",
            "lightning_damage_resistance_%" => "base_lightning_damage_resistance_%",
            "chaos_damage_resistance_%" => "base_chaos_damage_resistance_%",
            "maximum_fire_damage_resistance_%" => "base_maximum_fire_damage_resistance_%",
            "maximum_cold_damage_resistance_%" => "base_maximum_cold_damage_resistance_%",
            "maximum_lightning_damage_resistance_%" => "base_maximum_lightning_damage_resistance_%",
            "spell_block_%" or "chance_to_block_spells_%" => "base_spell_block_%",
            "enemy_extra_damage_rolls" => "base_enemy_extra_damage_rolls",
            "spell_suppression_chance_%" => "base_spell_suppression_chance_%",
            "minimum_added_spell_fire_damage" => "spell_minimum_added_fire_damage",
            "maximum_added_spell_fire_damage" => "spell_maximum_added_fire_damage",
            "minimum_added_spell_cold_damage" => "spell_minimum_added_cold_damage",
            "maximum_added_spell_cold_damage" => "spell_maximum_added_cold_damage",
            "minimum_added_spell_lightning_damage" => "spell_minimum_added_lightning_damage",
            "maximum_added_spell_lightning_damage" => "spell_maximum_added_lightning_damage",
            _ => plain,
        };
        return exists(plain) ? plain : null;
    }

    /// <summary>The real consumer names for a conditional stat that expands to more than one pool input.</summary>
    public static IReadOnlyList<string> FoldAll(string statId, Func<string, bool> exists)
    {
        if (statId == AllResistancesPerActiveNonVaalMinion)
            return [.. new[] { "base_resist_all_elements_%", "base_chaos_damage_resistance_%" }.Where(exists)];
        if (statId == GlobalDefencesPerActiveNonVaalMinion)
            return exists("global_defences_+%") ? ["global_defences_+%"] : [];
        if (statId == "attack_and_movement_speed_+%_if_you_have_beast_minion")
            return [.. new[] { "attack_speed_+%", "base_movement_velocity_+%" }.Where(exists)];
        if (statId == "avoid_freeze_and_chill_%_if_you_have_used_a_fire_skill_recently")
            return [.. new[] { "base_avoid_freeze_%", "base_avoid_chill_%" }.Where(exists)];
        if (PlainOf(statId) == "avoid_elemental_damage_%")
            return [.. new[] { "avoid_fire_damage_%", "avoid_cold_damage_%", "avoid_lightning_damage_%" }.Where(exists)];
        if (PlainOf(statId) == "all_resistances_%")
            return [.. new[] { "base_resist_all_elements_%", "base_chaos_damage_resistance_%" }.Where(exists)];
        if (PlainOf(statId) == "life_and_energy_shield_recovery_rate_+%")
            return [.. new[] { "life_recovery_rate_+%", "energy_shield_recovery_rate_+%" }.Where(exists)];
        if (PlainOf(statId) == "unaffected_by_ignite_and_shock")
            return [.. new[] { "unaffected_by_ignite", "unaffected_by_shock" }.Where(exists)];
        if (PlainOf(statId) == "immune_to_freeze_and_chill")
            return [.. new[] { "base_immune_to_freeze", "base_immune_to_chill" }.Where(exists)];
        if (PlainOf(statId) == "cannot_be_chilled_or_frozen")
            return [.. new[] { "base_cannot_be_chilled", "base_cannot_be_frozen" }.Where(exists)];
        if (PlainOf(statId) == "cannot_be_shocked_or_ignited")
            return [.. new[] { "base_cannot_be_shocked", "base_cannot_be_ignited" }.Where(exists)];
        return Fold(statId, exists) is { } plain ? [plain] : [];
    }

    public static int ValueForFold(string statId, int value) => PlainOf(statId) switch
    {
        "extra_damage_taken_from_crit_+%" => ActorScale.Saturate(-(long)value),
        "intimidate_enemies_on_hit" or "inflict_withered_for_2_seconds_on_hit" or "inflict_fire_exposure"
            or "inflict_cold_exposure" or "inflict_lightning_exposure" => value == 0 ? 0 : 100,
        _ => value,
    };

    // Suffixes that name a weapon/shield subject. Checked longest first so the specific one wins.
    const string DualWield = "_while_dual_wielding";
    const string WieldingShield = "_while_wielding_shield";
    const string UsingShield = "_while_using_a_shield";
    const string HoldingShield = "_while_holding_shield";
    const string HoldingAShield = "_while_holding_a_shield";
    const string TwoHanded = "_while_wielding_two_handed";
    const string UsingTwoHanded = "_while_using_two_handed";
    const string OneHanded = "_while_wielding_one_handed";
    const string UsingOneHanded = "_while_using_one_handed";

    const string WhileAffectedBy = "_while_affected_by_";
    const string IfAffectedBy = "_if_affected_by_";

    /// <summary>
    /// The "recently" phrase a stat id uses, the event <see cref="Subject.Recent"/> answers it with, and whether the id
    /// asks for that event's <em>absence</em>. Several phrases name the same event, so they collapse to one key
    /// (<c>hit_an_enemy</c> / <c>enemy_hit</c> are <see cref="HitEnemy"/>, while bare <c>hit</c> is
    /// <see cref="BeenHit"/>); a "not"/"haven't" phrase
    /// names its event with <c>Negated</c> set, which the caller reads as "the event did not happen". Longest phrase
    /// first, so <c>enemy_not_killed</c> wins over <c>killed</c>.
    /// </summary>
    static readonly (string Phrase, string Key, bool Negated)[] RecentEvents =
    [
        ("enemy_not_killed", KilledEnemy, true), ("enemy_shattered", ShatteredEnemy, false),
        ("enemy_killed", KilledEnemy, false), ("killed_cursed_enemy", KilledCursedEnemy, false),
        ("havent_killed", KilledEnemy, true), ("killed", KilledEnemy, false),
        ("have_crit", Crit, false), ("dealt_critical_strike", Crit, false), ("not_blocked", Blocked, true),
        ("blocked_an_attack", BlockedAttack, false), ("blocked_attack", BlockedAttack, false),
        ("blocked_a_spell", BlockedSpell, false), ("blocked_spell", BlockedSpell, false),
        ("blocked_a_stun", BlockedStun, false), ("have_blocked", Blocked, false),
        ("blocked", Blocked, false), ("suppressed_spell", SuppressedSpell, false),
        ("suppressed_spell_damage", SuppressedSpell, false), ("consumed_corpse", ConsumedCorpse, false),
        ("consumed_a_corpse", ConsumedCorpse, false),
        ("taken_a_savage_hit", TakenSavageHit, false), ("not_hit", BeenHit, true), ("have_not_been_hit", BeenHit, true),
        ("have_been_hit", BeenHit, false), ("been_hit", BeenHit, false), ("hit_an_enemy", HitEnemy, false),
        ("enemy_hit", HitEnemy, false), ("been_stunned", BeenStunned, false), ("been_frozen", BeenFrozen, false),
        ("used_a_fire_skill", UsedFireSkill, false), ("used_a_cold_skill", UsedColdSkill, false),
        ("used_a_warcry", UsedWarcry, false), ("warcried", UsedWarcry, false),
        ("used_movement_skill", UsedMovementSkill, false), ("used_retaliation", UsedRetaliation, false),
        ("used_a_vaal_skill", UsedVaalSkill, false),
        ("cast_dash", UsedDash, false), ("used_dash", UsedDash, false),
        ("used_ruby_flask", UsedRubyFlask, false), ("used_sapphire_flask", UsedSapphireFlask, false),
        ("used_topaz_flask", UsedTopazFlask, false), ("used_amethyst_flask", UsedAmethystFlask, false),
        ("dealt_non_crit", NonCrit, false), ("dealt_non_critical_strike", NonCrit, false),
        ("taunted_an_enemy", TauntedEnemy, false), ("detonated_mines", DetonatedMine, false),
        ("detonated_mine", DetonatedMine, false),
        ("frozen_enemy", FrozeEnemy, false), ("shocked", ShockedEnemy, false), ("ignited_an_enemy", IgnitedEnemy, false),
        ("gained_frenzy_charge", GainedFrenzyCharge, false), ("gained_power_charge", GainedPowerCharge, false),
        ("gained_endurance_charge", GainedEnduranceCharge, false),
        ("used_a_skill", UsedSkill, false), ("spent_mana", SpentMana, false),
        ("crit", Crit, false), ("hit", BeenHit, false),
    ];

    /// <summary>The recent-event keys <see cref="Subject.Recent"/> carries, as this class uses them.</summary>
    public const string HitEnemy = "hit_enemy", KilledEnemy = "killed_enemy", KilledCursedEnemy = "killed_cursed_enemy",
        ShatteredEnemy = "shattered_enemy", FrozeEnemy = "froze_enemy", Crit = "crit", Blocked = "blocked",
        BlockedStun = "blocked_stun", TakenSavageHit = "taken_savage_hit", BeenHit = "been_hit", BeenStunned = "been_stunned",
        BeenFrozen = "been_frozen", SuppressedSpell = "suppressed_spell", ConsumedCorpse = "consumed_corpse",
        UsedFireSkill = "used_fire_skill", UsedColdSkill = "used_cold_skill", UsedWarcry = "used_warcry",
        UsedMovementSkill = "used_movement_skill", UsedRetaliation = "used_retaliation", TauntedEnemy = "taunted_enemy",
        UsedVaalSkill = "used_vaal_skill", UsedDash = "used_dash", UsedRubyFlask = "used_ruby_flask",
        UsedSapphireFlask = "used_sapphire_flask", UsedTopazFlask = "used_topaz_flask", UsedAmethystFlask = "used_amethyst_flask",
        NonCrit = "non_crit", Crit8Seconds = "crit_8_seconds",
        DetonatedMine = "detonated_mine", UsedSkill = "used_skill", SpentMana = "spent_mana",
        ChangedStance = "changed_stance",
        GainedFrenzyCharge = "gained_frenzy_charge", GainedPowerCharge = "gained_power_charge",
        GainedEnduranceCharge = "gained_endurance_charge",
        BlockedAttack = "blocked_attack", BlockedSpell = "blocked_spell", ShockedEnemy = "shocked_enemy", IgnitedEnemy = "ignited_enemy";

    /// <summary>
    /// The condition of one stat id against one subject.
    /// <para>
    /// The families are the ones issue #504 names. A weapon or shield condition follows the weapons held; a
    /// <c>while affected by</c> one follows the auras running; a <c>recently</c> one follows the events the Subject
    /// reports for the last four seconds, and reads "not X" as the absence of X; an "if you have at least N X" one
    /// compares the attribute the Subject reads. Every one of them is <see cref="State.NotModelled"/> when the subject
    /// cannot answer it, so a caller without that state keeps its old behaviour.
    /// </para>
    /// <para>
    /// Per-charge and on-kill-grant ids are deliberately not decided here: <c>ChargeEffects</c> expands the
    /// <c>_per_frenzy_charge</c> / <c>per_power_charge</c> / <c>per_endurance_charge</c> coefficients against the counts
    /// and <c>ChargeGains</c> hands the charges out, both already, and editing the same ids twice would count them
    /// twice.
    /// </para>
    /// </summary>
    static System.Collections.Concurrent.ConcurrentDictionary<string, Func<Subject, State>>? ParsedConditions;

    public static State Of(string statId, in Subject subject) =>
        System.Threading.LazyInitializer.EnsureInitialized(ref ParsedConditions,
            static () => new(StringComparer.Ordinal)).GetOrAdd(statId, ParseCondition)(subject);

    // Parse the immutable stat id once; the predicate still reads the live subject on every call.
    static Func<Subject, State> ParseCondition(string statId)
    {
        var property = EquipmentPropertyThresholdId.Match(statId);
        if (property.Success)
        {
            string prefix = statId[..property.Index];
            string propertyKey = property.Groups["slot"].Value + "_" + property.Groups["property"].Value;
            bool valid = int.TryParse(property.Groups["n"].Value, out int minimum);
            return subject => And(Of(prefix, subject), valid && subject.Value?.Invoke(propertyKey) is int actual
                ? actual >= minimum ? State.Holds : State.Fails : State.NotModelled);
        }
        var ring = RingConditionId.Match(statId);
        if (ring.Success)
        {
            string ringKey = ring.Value[4..]; // Drop _if_; the source-aware value reader owns slot relationships.
            string prefix = statId[..ring.Index];
            return subject => And(Of(prefix, subject), subject.Value?.Invoke(ringKey) is int value
                ? value != 0 ? State.Holds : State.Fails : State.NotModelled);
        }
        var difference = ResourceDifferenceId.Match(statId);
        if (difference.Success)
        {
            string leftResource = difference.Groups["left"].Value, rightResource = difference.Groups["right"].Value;
            string prefix = statId[..difference.Index];
            bool valid = long.TryParse(difference.Groups["n"].Value, out long limit);
            return subject => And(Of(prefix, subject), valid
                && subject.Value?.Invoke(leftResource) is int left && subject.Value?.Invoke(rightResource) is int right
                    ? Math.Abs((long)left - right) <= limit ? State.Holds : State.Fails : State.NotModelled);
        }
        var below = BelowThresholdId.Match(statId);
        if (below.Success)
        {
            string belowAttribute = below.Groups["attr"].Value;
            string prefix = statId[..below.Index];
            bool valid = int.TryParse(below.Groups["n"].Value, out int limit);
            return subject => And(Of(prefix, subject), valid && subject.Value?.Invoke(belowAttribute) is int actual
                ? actual < limit ? State.Holds : State.Fails : State.NotModelled);
        }
        if (CandidateCatFlag(statId) is { } catFlag)
            return subject => subject.Flags is { } flags && flags.TryGetValue(catFlag, out bool active)
                ? active ? State.Holds : State.Fails : State.NotModelled;
        var collection = EquipmentCollectionId.Match(statId);
        if (collection.Success)
        {
            string prefix = statId[..collection.Index], countKey = "equipped_" + collection.Groups["kind"].Value;
            bool all = collection.Groups["mode"].Value == "all";
            return subject =>
            {
                State state = State.NotModelled;
                if (subject.Value?.Invoke(countKey) is int count)
                {
                    if (!all) state = count == 0 ? State.Holds : State.Fails;
                    else if (subject.Value("equipped_total") is int total)
                        state = total > 0 && count == total ? State.Holds : State.Fails;
                }
                return And(Of(prefix, subject), state);
            };
        }
        if (IsActiveNonVaalSkillMinionCoefficient(statId))
            return subject => subject.Amounts is { } amounts
                && amounts.ContainsKey(ActiveNonVaalSkillMinionCount) ? State.Holds : State.Fails;
        var equipment = EquipmentThresholdId.Match(statId);
        if (equipment.Success)
        {
            string prefixId = statId[..equipment.Index];
            string countKey = "equipped_" + equipment.Groups["kind"].Value;
            bool hasMinimum = int.TryParse(equipment.Groups["n"].Value, out int minimumItems);
            return subject =>
            {
                State count = hasMinimum && subject.Value?.Invoke(countKey) is int actual
                    ? actual >= minimumItems ? State.Holds : State.Fails : State.NotModelled;
                State prefix = Of(prefixId, subject);
                return And(prefix, count);
            };
        }
        var comparison = AttributeComparisonId.Match(statId);
        if (comparison.Success)
        {
            string leftKey = comparison.Groups["left"].Value;
            string rightKey = comparison.Groups["right"].Value;
            string comparisonOperator = comparison.Groups["op"].Value;
            return subject =>
            {
                int? left = subject.Value?.Invoke(leftKey);
                int? right = subject.Value?.Invoke(rightKey);
                if (left is null || right is null) return State.NotModelled;
                bool holds = comparisonOperator switch
                {
                    "higher" => left > right, "lower" => left < right, _ => left == right,
                };
                return holds ? State.Holds : State.Fails;
            };
        }
        var equippedAmount = EquipmentAmountId.Match(statId);
        if (equippedAmount.Success)
        {
            string countKey = "equipped_" + equippedAmount.Groups["kind"].Value;
            string prefix = statId[..equippedAmount.Index];
            return subject => And(Of(prefix, subject),
                subject.Value?.Invoke(countKey) is not null ? State.Holds : State.NotModelled);
        }
        if (TryRecentAmount(statId, out string amountKey, out _, out _))
            return subject => subject.Amounts is { } amounts && amounts.ContainsKey(amountKey) ? State.Holds : State.NotModelled;
        if (SlotConditionSuffixes.Take(2).Any(s => statId.EndsWith(s, StringComparison.Ordinal)))
            return static subject => subject.OffHandEmpty is bool empty
                ? empty ? State.Holds : State.Fails : State.NotModelled;
        if (statId.EndsWith(MagicRingCondition, StringComparison.Ordinal))
            return static subject => subject.Value?.Invoke("magic_ring_equipped") is int rings
                ? rings > 0 ? State.Holds : State.Fails : State.NotModelled;
        if (statId.EndsWith(MagicItemCondition, StringComparison.Ordinal))
            return static subject => subject.Value?.Invoke("equipped_magic") is int magic
                ? magic > 0 ? State.Holds : State.Fails : State.NotModelled;
        if (statId.EndsWith(NormalItemCondition, StringComparison.Ordinal))
            return static subject => subject.Value?.Invoke("equipped_normal") is int normal
                ? normal > 0 ? State.Holds : State.Fails : State.NotModelled;
        if (statId.EndsWith(EquippedAxeCondition, StringComparison.Ordinal))
            return static subject => subject.Axe is bool axe
                ? axe ? State.Holds : State.Fails : State.NotModelled;
        var counted = RecentCountThreshold.Match(statId);
        if (counted.Success)
        {
            bool hasMinimum = long.TryParse(counted.Groups["n"].Value, out long minimum);
            return subject => hasMinimum && subject.Amounts is { } counts && counts.TryGetValue(KilledEnemy, out long kills)
                ? kills >= minimum ? State.Holds : State.Fails : State.NotModelled;
        }
        if (statId.Contains(DualWield, StringComparison.Ordinal))
            return static subject => subject.DualWield is bool d ? (d ? State.Holds : State.Fails) : State.NotModelled;
        if (statId.Contains(WieldingShield, StringComparison.Ordinal) ||
            statId.Contains(UsingShield, StringComparison.Ordinal) ||
            statId.Contains(HoldingShield, StringComparison.Ordinal) ||
            statId.Contains(HoldingAShield, StringComparison.Ordinal))
            return static subject => subject.Shield is bool s ? (s ? State.Holds : State.Fails) : State.NotModelled;
        if (statId.Contains(TwoHanded, StringComparison.Ordinal) ||
            statId.Contains(UsingTwoHanded, StringComparison.Ordinal))
            return static subject => subject.TwoHanded is bool t ? (t ? State.Holds : State.Fails) : State.NotModelled;
        if (statId.Contains(OneHanded, StringComparison.Ordinal) ||
            statId.Contains(UsingOneHanded, StringComparison.Ordinal))
            return static subject => subject.OneHanded is bool o ? (o ? State.Holds : State.Fails) : State.NotModelled;

        if (StateSuffix(statId) is { } stateSuffix)
            return subject => subject.Flags is { } stateFlags && stateFlags.TryGetValue(stateSuffix, out bool stateActive)
                ? stateActive ? State.Holds : State.Fails : State.NotModelled;

        // "while affected by X": X is a buff the character may carry. Only an ordinary aura the server already runs can
        // be answered; a shrine or a league buff is a subject it does not model yet, so those stats keep their
        // previous behaviour rather than being treated as "the condition is false".
        if (TryAuraName(statId, out string family))
        {
            if (!IsModelledAura(family)) return static _ => State.NotModelled;
            return subject => subject.Auras is { } running
                ? AuraHolds(family, running) ? State.Holds : State.Fails : State.NotModelled;
        }

        // "recently": the event happened within the last four seconds. The subject carries the events that did.
        var eightSecond = RecentEightSecondsId.Match(statId);
        if (eightSecond.Success)
        {
            return subject =>
            {
                if (subject.Recent is not { } recentEight
                    || subject.KnownRecent is { } knownEight && !knownEight.Contains(Crit8Seconds)) return State.NotModelled;
                return recentEight.Contains(Crit8Seconds) ? State.Holds : State.Fails;
            };
        }
        if (TryRecentEvent(statId, out string key, out bool negated))
        {
            return subject =>
            {
                if (subject.Recent is not { } recent || subject.KnownRecent is { } known && !known.Contains(key)) return State.NotModelled;
                bool happened = recent.Contains(key);
                return (negated ? !happened : happened) ? State.Holds : State.Fails;
            };
        }

        // "if you have at least N <attribute>": compare the attribute the subject reads.
        if (TryThreshold(statId, out string attribute, out int needed))
        {
            return subject => subject.Value is { } value && value(attribute) is int actual
                ? actual >= needed ? State.Holds : State.Fails : State.NotModelled;
        }

        // Per-charge coefficients and on-kill grants are ChargeEffects' and ChargeGains' already: an id naming a charge
        // type is left to them, not folded here, so the same value is not counted twice.
        if (NamesACharge(statId)) return static _ => State.NotModelled;

        // Anything else that still looks conditional is not modelled: keep it as it was, and let a caller list it.
        if (statId.Contains(WhileAffectedBy, StringComparison.Ordinal) ||
            statId.Contains(IfAffectedBy, StringComparison.Ordinal) ||
            statId.Contains("_while_", StringComparison.Ordinal) ||
            statId.Contains("_if_you_have_", StringComparison.Ordinal) ||
            statId.Contains("_if_", StringComparison.Ordinal))
            return static _ => State.NotModelled;

        return static _ => State.None;
    }

    /// <summary>Whether an id is one ChargeEffects (<c>_per_&lt;type&gt;_charge</c>) or ChargeGains
    /// (<c>add_&lt;type&gt;_charge_on_kill</c>) already owns.</summary>
    public static bool NamesACharge(string statId) =>
        PerChargeType.IsMatch(statId) || AddChargeOnKill.IsMatch(statId);

    static readonly System.Text.RegularExpressions.Regex PerChargeType =
        new(@"^(?:.*_)?(?:per|current|minimum|maximum)_(?:frenzy|power|endurance|inspiration|challenger|siphoning|brutal|absorption|affliction)_charges?", System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex AddChargeOnKill =
        new(@"^add_(?:frenzy|power|endurance|inspiration|challenger|brutal|absorption)_charge_on_kill(?:_|$)", System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex RecentId =
        new(@"_if_(?:you_)?(?:have_)?(?<negation>havent_|not_)?(?<phrase>[a-z_]+?)_recently(?=$|_final$|_\+%$|_%$)", System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex RecentEightSecondsId =
        new(@"_if_(?:you_)?(?:have_)?crit_in_past_8_seconds$", System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex RecentCountThreshold =
        new(@"_if_(?:you_have_)?killed_at_least_(?<n>\d+)_enemies_recently$", System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex EquipmentPropertyThresholdId =
        new(@"_if_(?<slot>shield)_has_(?<n>\d+)%_(?<property>block_chance)$", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex EquipmentThresholdId =
        new(@"_if_(?<n>\d+)_(?<kind>[a-z_]+)_items$", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex EquipmentAmountId =
        new(@"_(?:per|for_each)_equipped_(?<kind>[a-z_]+?)(?:_item)?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex EquipmentCollectionId =
        new(@"_with_(?<mode>no|all)_(?<kind>[a-z_]+?)_equipped_items$", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex AttributeComparisonId =
        new(@"_if_(?<left>strength|dexterity|intelligence)_(?<op>higher|lower|equal)_than_(?<right>strength|dexterity|intelligence)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex RingConditionId =
        new(@"_if_(?:other_ring_is_(?:shaper|elder)_item|(?:left|right)_ring_is_magic_item)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // These table-local values describe a relationship to the source, then project onto the recipient's stats.
    public static bool SourceLocalCondition(string id) => RingConditionId.IsMatch(id)
        && id.Contains("_if_other_ring_", StringComparison.Ordinal);

    static State And(State left, State right) => left is State.Fails || right is State.Fails ? State.Fails
        : left is State.NotModelled || right is State.NotModelled ? State.NotModelled
        : left is State.Holds || right is State.Holds ? State.Holds : State.None;

    static readonly System.Text.RegularExpressions.Regex RecentAmountId =
        new(@"_per_(?:(?<unit>\d+)_)?(?<event>enemy_killed|mana_spent)_recently(?:_up_to_(?<cap>\d+)%)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool TryRecentAmount(string id, out string key, out long unit, out int? cap)
    {
        key = ""; unit = 1; cap = null;
        var match = RecentAmountId.Match(id);
        if (!match.Success) return false;
        if (match.Groups["unit"].Success && (!long.TryParse(match.Groups["unit"].Value, out unit) || unit <= 0)) return false;
        if (match.Groups["cap"].Success)
        {
            if (!int.TryParse(match.Groups["cap"].Value, out int maximum)) return false;
            cap = maximum;
        }
        key = match.Groups["event"].Value == "enemy_killed" ? KilledEnemy : SpentMana;
        return true;
    }

    public static int ApplyRecentAmount(string id, int value, in Subject subject)
    {
        if (IsActiveNonVaalSkillMinionCoefficient(id))
        {
            long minions = subject.Amounts?.GetValueOrDefault(ActiveNonVaalSkillMinionCount) ?? 0;
            return (int)Int128.Clamp((Int128)value * Math.Max(0, minions), int.MinValue, int.MaxValue);
        }
        var equipment = EquipmentAmountId.Match(id);
        if (equipment.Success && subject.Value?.Invoke("equipped_" + equipment.Groups["kind"].Value) is int count)
            return (int)Math.Clamp((long)value * Math.Max(0, count), int.MinValue, int.MaxValue);
        if (!TryRecentAmount(id, out string key, out long unit, out int? cap)
            || subject.Amounts is not { } amounts || !amounts.TryGetValue(key, out long total)) return value;
        Int128 scaled = (Int128)value * (Math.Max(0, total) / unit);
        if (cap is int maximum && scaled > maximum) scaled = maximum;
        return (int)Int128.Clamp(scaled, int.MinValue, int.MaxValue);
    }

    /// <summary>The event a <c>recently</c> id names, and whether the id asks for the event's absence.</summary>
    public static bool TryRecentEvent(string statId, out string key, out bool negated)
    {
        key = ""; negated = false;
        if (TryRecentCast(statId, out string spell, out int seconds))
        {
            key = CastEventKey(spell, seconds);
            return true;
        }
        var m = RecentId.Match(statId);
        if (!m.Success) return false;
        string phrase = m.Groups["phrase"].Value;
        // A "not"/"haven't" prefix negates whatever event the rest of the phrase names.
        bool phraseNegated = phrase.StartsWith("not_", StringComparison.Ordinal)
            || phrase.StartsWith("havent_", StringComparison.Ordinal);
        bool writtenNegated = m.Groups["negation"].Success ^ phraseNegated;
        string bare = phraseNegated ? phrase[(phrase.IndexOf('_') + 1)..] : phrase;
        foreach (var (p, k, tableNegated) in RecentEvents)
            if (bare == p)
            {
                key = k;
                negated = writtenNegated ^ tableNegated;
                return true;
            }
        return false;
    }

    static readonly System.Text.RegularExpressions.Regex RecentCastId =
        new(@"_if_cast_(?<spell>[a-z_]+)_in_past_(?<seconds>\d+)_seconds$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string CastEventKey(string spell, int seconds) => $"cast_{spell}_past_{seconds}_seconds";

    public static bool TryRecentCast(string statId, out string spell, out int seconds)
    {
        var match = RecentCastId.Match(statId);
        spell = match.Success ? match.Groups["spell"].Value : "";
        seconds = 0;
        return match.Success && int.TryParse(match.Groups["seconds"].Value, out seconds) && seconds > 0;
    }

    static readonly System.Text.RegularExpressions.Regex ThresholdId =
        new(@"_(?:if_you_have|while(?:_you_have)?)_at_least_(?<n>\d+)_(?<attr>maximum_energy_shield|strength|dexterity|intelligence|rage|mana|life)(?=$|_final$|_\+%$|_%$)", System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex ResourceDifferenceId =
        new(@"_while_max_(?<left>life|mana|energy_shield)_(?<right>life|mana|energy_shield)_within_(?<n>\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex BelowThresholdId =
        new(@"_if_below_(?<n>\d+)_(?<attr>strength|dexterity|intelligence)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The attribute and the minimum an "if you have at least N X" id asks for.</summary>
    public static bool TryThreshold(string statId, out string attribute, out int needed)
    {
        attribute = ""; needed = 0;
        var m = ThresholdId.Match(statId);
        if (!m.Success || !int.TryParse(m.Groups["n"].Value, out needed)) return false;
        attribute = m.Groups["attr"].Value switch
        {
            "strength" => "strength", "dexterity" => "dexterity", "intelligence" => "intelligence",
            "rage" => "rage", "mana" => "mana", "life" => "life",
            "maximum_energy_shield" => "energy_shield",
            _ => "",
        };
        return attribute.Length > 0;
    }

    /// <summary>The subject a <c>while_affected_by_X</c> / <c>if_affected_by_X</c> stat names, without its qualifiers.</summary>
    public static bool TryAuraName(string statId, out string family)
    {
        family = "";
        int at = statId.IndexOf(WhileAffectedBy, StringComparison.Ordinal);
        int skip = WhileAffectedBy.Length;
        if (at < 0)
        {
            at = statId.IndexOf(IfAffectedBy, StringComparison.Ordinal);
            skip = IfAffectedBy.Length;
        }
        if (at < 0) return false;
        family = statId[(at + skip)..];
        foreach (string unit in TrailingUnits)
            if (family.EndsWith(unit, StringComparison.Ordinal))
            {
                family = family[..^unit.Length];
                break;
            }
        return family.Length > 0;
    }

    /// <summary>Whether the named family holds: an exact name, or an <c>X_and_Y</c> list where all must hold.</summary>
    public static bool AuraHolds(string family, IReadOnlySet<string> running)
    {
        var parts = family.Split("_and_", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts) if (!running.Contains(part)) return false;
        return true;
    }

    /// <summary>Every aura family name the given client state is running, as the stat ids spell them.</summary>
    public static readonly ConcurrentDictionary<string, IReadOnlySet<string>> Cache = new();

    /// <summary>The ordinary auras a stat id can name, lower-cased as the ids spell them. This is the server's own
    /// ordinary aura set (<c>AuraCatalog</c>), so a name outside it is a subject nothing models yet.</summary>
    static readonly HashSet<string> ModelledAuras = new(StringComparer.Ordinal)
    {
        "clarity", "vitality", "precision", "haste", "determination", "grace", "discipline",
        "anger", "hatred", "wrath", "zealotry", "malevolence", "envy", "pride",
        "cat_aspect",
        "vulnerability",
        "purity_of_elements", "purity_of_fire", "purity_of_ice", "purity_of_lightning",
        "a_herald", "herald_of_ash", "herald_of_ice", "herald_of_thunder", "herald_of_purity", "herald_of_agony",
    };

    /// <summary>Whether every name in a <c>X_and_Y</c> family is an aura the server runs.</summary>
    public static bool IsModelledAura(string family) =>
        family.Split("_and_", StringSplitOptions.RemoveEmptyEntries).All(p => ModelledAuras.Contains(p));

    /// <summary>The stat-id spelling of an aura the server runs, from an <c>AuraCatalog</c> effect id ("Grace" → "grace").
    /// Null when it is not an ordinary aura this build knows.</summary>
    public static string? StatNameOf(string effectId)
    {
        if (string.IsNullOrEmpty(effectId)) return null;
        string family = AuraCatalog.Family(effectId);
        var snake = new System.Text.StringBuilder(family.Length + 4);
        foreach (char c in family)
        {
            if (char.IsUpper(c) && snake.Length > 0) snake.Append('_');
            snake.Append(char.ToLowerInvariant(c));
        }
        string name = snake.ToString();
        return ModelledAuras.Contains(name) ? name : null;
    }

    static string? CandidateCatFlag(string statId)
    {
        foreach (var (flag, tokens) in new[]
        {
            ("has_cat_aspect", new[] { "_while_you_have_cat_aspect", "_while_have_cat_aspect",
                "_if_you_have_cat_aspect", "_while_affected_by_cat_aspect", "_if_affected_by_cat_aspect" }),
            ("has_cats_stealth", new[] { "_while_you_have_cats_stealth", "_while_have_cats_stealth",
                "_if_you_have_cats_stealth", "_while_affected_by_cats_stealth", "_if_affected_by_cats_stealth",
                "_while_you_have_cat_stealth" }),
            ("has_cats_agility", new[] { "_while_you_have_cats_agility", "_while_have_cats_agility",
                "_if_you_have_cats_agility", "_while_affected_by_cats_agility", "_if_affected_by_cats_agility" }),
        })
            if (statId == flag || tokens.Any(token => EndsWithCondition(statId, token)))
                return flag;
        return null;
    }

    /// <summary>Builds the aura set a stat's <c>while_affected_by_X</c> is answered from: every ordinary aura the given
    /// names cover, in stat-id spelling.</summary>
    public static IReadOnlySet<string> AuraSet(IEnumerable<string> effectIds)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in effectIds)
            if (StatNameOf(id) is { } name)
            {
                set.Add(name);
                if (name.StartsWith("herald_of_", StringComparison.Ordinal)) set.Add("a_herald");
            }
        return set;
    }

    public const string ActiveNonVaalSkillMinionCount = "active_non_vaal_skill_minion_count";
    const string AllResistancesPerActiveNonVaalMinion =
        "all_resistances_%_per_active_minion_not_from_vaal_skills";
    const string GlobalDefencesPerActiveNonVaalMinion =
        "global_defences_+%_per_active_minion_not_from_vaal_skills";

    public static bool IsActiveNonVaalSkillMinionCoefficient(string id) =>
        id is AllResistancesPerActiveNonVaalMinion or GlobalDefencesPerActiveNonVaalMinion;

    /// <summary>What a caller can answer about the character a stat is being summed for. Null fields are unknown.</summary>
    public readonly record struct Subject(
        bool? DualWield = null, bool? Shield = null, bool? TwoHanded = null, bool? OneHanded = null,
        bool? OffHandEmpty = null, bool? Axe = null,
        IReadOnlySet<string>? Auras = null,
        IReadOnlySet<string>? Recent = null,
        Func<string, int?>? Value = null,
        IReadOnlySet<string>? KnownRecent = null,
        IReadOnlyDictionary<string, bool>? Flags = null,
        IReadOnlyDictionary<string, long>? Amounts = null,
        Func<Item, string, int?>? ItemValue = null)
    {
        /// <summary>Nothing known: every condition is NotModelled and no stat changes.</summary>
        public static Subject Unknown => new();
    }
}
