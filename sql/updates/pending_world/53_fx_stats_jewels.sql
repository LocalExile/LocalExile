-- Radius jewels: these lines are carried out by the jewel catalogue (world.db radius_jewels, Progression's radius
-- pass), found by reading the catalogue's own conversion mods rather than by stat name.
UPDATE fx_stats SET handled = 'item', ref = 'radius_jewels (RadiusJewelCatalogue)', why = NULL, fit = NULL
WHERE handled = 'none' AND stat IN (
  'local_jewel_nearby_passives_dex_to_int',
  'local_jewel_nearby_passives_dex_to_str',
  'local_jewel_nearby_passives_int_to_dex',
  'local_jewel_nearby_passives_int_to_str',
  'local_jewel_nearby_passives_str_to_dex',
  'local_jewel_nearby_passives_str_to_int',
  'local_unique_jewel_notable_passives_in_radius_instead_grant_mana_cost_+%',
  'local_unique_jewel_notable_passives_in_radius_instead_grant_minion_damage_taken_+%',
  'local_unique_jewel_notable_passives_in_radius_instead_grant_minion_movement_speed_+%',
  'local_unique_jewel_notable_passives_in_radius_instead_grant_spell_damage_+%',
  'local_unique_jewel_passives_in_radius_give_trap_and_mine_maximum_added_physical_damage',
  'local_unique_jewel_passives_in_radius_give_trap_and_mine_minimum_added_physical_damage',
  'local_unique_jewel_passives_in_radius_grant_additional_all_attributes',
  'local_unique_jewel_passives_in_radius_grant_base_chaos_damage_resistance_%',
  'local_unique_jewel_passives_in_radius_grant_base_maximum_life',
  'local_unique_jewel_passives_in_radius_grant_base_maximum_mana',
  'local_unique_jewel_passives_in_radius_grant_chaos_damage_+%',
  'local_unique_jewel_passives_in_radius_grant_cold_damage_+%',
  'local_unique_jewel_passives_in_radius_grant_critical_strike_chance_+%',
  'local_unique_jewel_passives_in_radius_grant_evasion_rating_+%',
  'local_unique_jewel_passives_in_radius_grant_fire_damage_+%',
  'local_unique_jewel_passives_in_radius_grant_lightning_damage_+%',
  'local_unique_jewel_passives_in_radius_grant_maximum_energy_shield_+%',
  'local_unique_jewel_passives_in_radius_grant_physical_damage_+%',
  'local_unique_jewel_passives_in_radius_grant_physical_damage_reduction_rating_+%');
-- Threshold jewels whose granted stat is itself read.
UPDATE fx_stats SET handled = 'item', ref = 'ThresholdJewels', why = NULL, fit = NULL
WHERE handled = 'none' AND stat IN (
  'local_unique_jewel_ethereal_knives_projectiles_nova_with_50_dex_in_radius',
  'local_unique_jewel_frost_blades_projectile_speed_+%_with_40_dex_in_radius',
  'local_unique_jewel_ground_slam_angle_+%_with_50_str_in_radius',
  'local_unique_jewel_shrapnel_shot_radius_+%_with_50_dex_in_radius',
  'local_unique_jewel_spark_projectiles_nova_with_40_int_in_radius');
-- Threshold jewels whose check works but whose granted stat nothing reads yet: say so.
UPDATE fx_stats SET why = 'The threshold in radius is checked (ThresholdJewels) and its stat is granted; no code reads that granted stat yet.'
WHERE handled = 'none' AND stat IN (
  'local_jewel_disable_combust_with_40_strength_in_radius',
  'local_jewel_infernal_cry_exerted_attacks_ignite_damage_+%_final_with_for_strength_in_radius',
  'local_jewel_magma_orb_damage_+%_final_per_chain_with_40_int_in_radius',
  'local_jewel_magma_orb_damage_+%_final_with_40_int_in_radius',
  'local_jewel_molten_strike_projectiles_chain_count_+_with_40_str_in_radius',
  'local_jewel_molten_strike_projectiles_chain_when_impacting_ground_with_40_str_in_radius',
  'local_jewel_molten_strike_projectiles_count_+%_final_with_40_str_in_radius',
  'local_unique_jewel_animate_weapon_can_animate_up_to_x_additional_ranged_weapons_with_50_dex_in_radius',
  'local_unique_jewel_barrage_final_volley_fires_x_additional_projectiles_simultaneously_with_50_dex_in_radius',
  'local_unique_jewel_blight_applies_wither_for_two_seconds_with_40_int_in_radius',
  'local_unique_jewel_blight_hinder_duration_+%_with_40_int_in_radius',
  'local_unique_jewel_cleave_+1_base_radius_per_nearby_enemy_up_to_10_with_40_str_in_radius',
  'local_unique_jewel_cleave_fortify_on_hit_with_50_str_in_radius',
  'local_unique_jewel_cold_snap_uses_gains_power_charges_instead_of_frenzy_with_40_int_in_radius',
  'local_unique_jewel_discharge_area_of_effect_+%_final_with_40_int_in_radius',
  'local_unique_jewel_discharge_cooldown_override_ms_with_40_int_in_radius',
  'local_unique_jewel_discharge_damage_+%_final_with_40_int_in_radius',
  'local_unique_jewel_dual_strike_intimidate_on_hit_while_wielding_axe_with_40_dex_in_radius',
  'local_unique_jewel_elemental_hit_50%_less_cold_damage_per_40_str_and_int',
  'local_unique_jewel_elemental_hit_50%_less_fire_damage_per_40_int_and_dex',
  'local_unique_jewel_elemental_hit_50%_less_lightning_damage_per_40_str_and_dex',
  'local_unique_jewel_elemental_hit_cannot_roll_cold_damage_with_40_int_+_str_in_radius',
  'local_unique_jewel_elemental_hit_cannot_roll_fire_damage_with_40_int_+_dex_in_radius',
  'local_unique_jewel_elemental_hit_cannot_roll_lightning_damage_with_40_dex_+_str_in_radius',
  'local_unique_jewel_ethereal_knives_number_of_additional_projectiles_with_50_dex_in_radius',
  'local_unique_jewel_freezing_pulse_damage_+%_if_enemy_shattered_recently_with_50_int_in_radius',
  'local_unique_jewel_freezing_pulse_number_of_additional_projectiles_with_50_int_in_radius',
  'local_unique_jewel_frost_blades_melee_damage_penetrates_%_cold_resistance_with_40_dex_in_radius',
  'local_unique_jewel_frostbolt_additional_projectiles_with_40_int_in_radius',
  'local_unique_jewel_frostbolt_projectile_acceleration_with_50_int_in_radius',
  'local_unique_jewel_galvanic_arrow_area_damage_+%_with_40_dex_in_radius',
  'local_unique_jewel_glacial_cascade_number_of_additional_bursts_with_40_int_in_radius',
  'local_unique_jewel_glacial_cascade_physical_damage_%_to_convert_to_cold_with_40_int_in_radius',
  'local_unique_jewel_glacial_hammer_item_rarity_on_shattering_enemy_+%_with_50_strength_in_radius',
  'local_unique_jewel_glacial_hammer_melee_splash_with_cold_damage_with_50_str_in_radius',
  'local_unique_jewel_glacial_hammer_physical_damage_%_to_convert_to_cold_with_50_str_in_radius',
  'local_unique_jewel_ground_slam_chance_to_gain_endurance_charge_%_on_stun_with_50_str_in_radius',
  'local_unique_jewel_heavy_strike_chance_to_deal_double_damage_%_with_50_strength_in_radius',
  'local_unique_jewel_ice_shot_explosion_skill_area_of_effect_+%_with_50_dex_in_radius',
  'local_unique_jewel_ice_shot_pierce_+_with_40_dex_in_radius',
  'local_unique_jewel_magma_orb_skill_area_of_effect_+%_per_bounce_with_50_int_in_radius',
  'local_unique_jewel_spark_number_of_additional_projectiles_with_40_int_in_radius',
  'local_unique_jewel_spectral_throw_damage_for_each_enemy_hit_with_spectral_weapon_+%_with_50_dexterity_in_radius',
  'local_unique_jewel_spectres_gain_soul_eater_on_kill_%_chance_with_50_int_in_radius',
  'local_unique_jewel_vigilant_strike_fortifies_nearby_allies_for_x_seconds_with_50_str_in_radius',
  'local_unique_jewel_viper_strike_attack_damage_per_poison_on_enemy_+%_with_50_dexterity_in_radius',
  'local_unique_jewel_viper_strike_chance_to_gain_unholy_might_%_on_hit_per_poison_stack_on_enemy_with_50_dex_in_radius',
  'local_unique_jewel_warcry_damage_taken_goes_to_mana_%_with_40_int_in_radius',
  'local_unique_jewel_zombie_slam_cooldown_speed_+%_with_50_int_in_radius',
  'local_unique_jewel_zombie_slam_damage_+%_with_50_int_in_radius');
-- Conduit shares charges with party members: there is nothing for it to do for a character playing alone.
UPDATE fx_stats SET handled = 'display', ref = 'party only', why = NULL, fit = NULL
WHERE stat IN ('keystone_conduit', 'endurance_only_conduit');
-- "Adds Secrets of Suffering": the cluster jewel adds the keystone (ClusterJewelStats.Keystone) and the keystone is built.
UPDATE fx_stats SET handled = 'item', ref = 'ClusterJewelStats.Keystone', why = NULL, fit = NULL
WHERE stat = 'local_jewel_expansion_keystone_secrets_of_suffering';
UPDATE fx_stats SET why = 'The cluster jewel adds the keystone to the tree (ClusterJewelStats.Keystone); the keystone itself is not built.'
WHERE handled = 'none' AND stat LIKE 'local_jewel_expansion_keystone_%';
-- Inner Conviction (Voll's Protector, and the keystone wherever it is taken): 3% more Spell Damage per Power Charge
-- (PlayerHitDamage.SpellMore) and Power Charges instead of Frenzy Charges (ChargeSession.Apply).
UPDATE fx_stats SET handled = 'code', ref = 'PlayerHitDamage.SpellMore, ChargeSession.Apply', why = NULL, fit = NULL
WHERE stat = 'keystone_quiet_might';
