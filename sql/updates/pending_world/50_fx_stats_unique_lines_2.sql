-- fx_stats only: single uniques' lines read by code from this change on.
-- "per Red / Green / Blue / White Socket" on the item with the line and "for each Empty ... Socket on any Equipped
-- Item" (SocketCountItemStats: The Pariah, Prismatic Eclipse, The Untouched Soul and their kin).
UPDATE fx_stats SET handled = 'code', code = 'SocketCountItemStats.cs', why = NULL, fit = NULL
WHERE stat IN ('base_maximum_life_per_red_socket_on_item', 'base_maximum_energy_shield_per_blue_socket_on_item',
  'base_maximum_mana_per_green_socket_on_item', 'item_found_rarity_+%_per_white_socket_on_item',
  'item_found_quantity_+%_per_white_socket_on_item', 'global_weapon_physical_damage_+%_per_red_socket_on_item',
  'global_attack_speed_+%_per_green_socket_on_item', 'global_mana_leech_from_physical_attack_damage_permyriad_per_blue_socket_on_item',
  'global_life_leech_from_physical_attack_damage_per_red_socket_on_item_permyriad', 'global_melee_range_+_per_white_socket_on_item',
  'global_critical_strike_mulitplier_+_per_green_socket_on_item', 'global_critical_strike_chance_+%_per_blue_socket_on_item',
  'global_defences_+%_per_white_socket_on_item', 'base_skill_area_of_effect_+%_per_red_socket_on_item',
  'maximum_life_+_per_empty_red_socket', 'accuracy_rating_+_per_empty_green_socket', 'maximum_mana_+_per_empty_blue_socket',
  'elemental_resistance_%_per_empty_white_socket');
-- Leech against an enemy in a state (Leech.WithStateLeech: Berek's Grip, Valako's Sign, Flesh-Eater, Taproot,
-- Dyadian Dawn, Thirst for Horrors).
UPDATE fx_stats SET handled = 'code', code = 'Leech.cs', why = NULL, fit = NULL
WHERE stat IN ('base_life_leech_permyriad_vs_shocked_enemies', 'energy_shield_leech_permyriad_vs_frozen_enemies',
  'life_leech_permyriad_vs_cursed_enemies', 'life_leech_from_attack_damage_permyriad_vs_bleeding_enemies',
  'life_leech_from_attack_damage_permyriad_vs_maimed_enemies', 'base_life_leech_from_attack_damage_permyriad_vs_chilled_enemies',
  'mana_leech_from_attack_damage_permyriad_vs_poisoned_enemies');
-- Added damage against an enemy in a state (ConditionalSupportDamage.WithStateAddedDamage: Tasalio's Sign, The
-- Crimson Storm, Stormfire, Doedre's Scorn, The Wasp Nest). Added after the roll, so the hit's increases do not
-- scale it, as the burning-enemy fire beside it.
UPDATE fx_stats SET handled = 'code', code = 'ConditionalSupportDamage.cs', why = NULL, fit = NULL
WHERE stat IN ('minimum_added_cold_damage_vs_chilled_enemies', 'maximum_added_cold_damage_vs_chilled_enemies',
  'minimum_added_physical_damage_vs_bleeding_enemies', 'maximum_added_physical_damage_vs_bleeding_enemies',
  'global_minimum_added_lightning_damage_vs_ignited_enemies', 'global_maximum_added_lightning_damage_vs_ignited_enemies',
  'minimum_added_chaos_damage_per_curse_on_enemy', 'maximum_added_chaos_damage_per_curse_on_enemy',
  'minimum_added_chaos_damage_vs_enemies_with_5+_poisons', 'maximum_added_chaos_damage_vs_enemies_with_5+_poisons');
-- "increased Attack / Melee Damage against Bleeding Enemies" at the character's own attack hit (Combat.cs; a
-- projectile's hit does not read it yet).
UPDATE fx_stats SET handled = 'code', code = 'Combat.cs', why = NULL, fit = NULL
WHERE stat IN ('attack_damage_vs_bleeding_enemies_+%', 'melee_damage_vs_bleeding_enemies_+%');
-- Attribute thresholds and The Iron Fortress's strength bonus (AttributeOffenceSources).
UPDATE fx_stats SET handled = 'code', code = 'AttributeOffenceSources.cs', why = NULL, fit = NULL
WHERE stat IN ('number_of_additional_mines_to_place_with_at_least_500_dex', 'number_of_additional_mines_to_place_with_at_least_500_int',
  'poison_damage_+%_with_over_300_dexterity', 'resist_all_elements_%_with_200_or_more_strength',
  'strength_damage_bonus_grants_melee_physical_damage_+3%_per_10_strength_instead');
-- Read already (GrandSpectrum builds the names): minimum charges per Grand Spectrum.
UPDATE fx_stats SET handled = 'code', code = 'GrandSpectrum.cs', why = NULL, fit = NULL
WHERE stat IN ('minimum_endurance_charges_per_stackable_unique_jewel', 'minimum_frenzy_charges_per_stackable_unique_jewel',
  'minimum_power_charges_per_stackable_unique_jewel');
-- Read already: these lines' skills come from the mod's own granted effect and are fired by a live rule on the
-- skill's trigger stat (GrantedSkillRules), checked by wearing each mod and asking the engine for its live rules.
UPDATE fx_stats SET handled = 'rule', ref = 'the granted skill''s own trigger (GrantedSkillRules)', why = NULL, fit = NULL
WHERE stat IN ('display_abberaths_hooves_skill_level',
  'local_display_attack_with_level_X_bone_nova_on_bleeding_enemy_kill',
  'local_display_cast_lightning_on_critical_strike',
  'local_display_molten_burst_on_melee_hit_%',
  'local_display_raise_spider_on_kill_%_chance',
  'local_display_starfall_on_melee_critical_hit_%',
  'local_display_summon_raging_spirit_on_kill_%',
  'local_display_trigger_commandment_of_inferno_on_crit_%',
  'local_display_trigger_level_18_summon_spectral_wolf_on_kill_10%_chance',
  'local_display_trigger_level_1_blood_rage_on_kill_chance_%',
  'local_display_trigger_level_20_shade_form_on_skill_use_%',
  'local_display_trigger_level_20_shade_form_when_hit_%',
  'local_display_trigger_level_X_blinding_aura_skill_on_equip',
  'local_display_trigger_level_X_shield_shatter_on_block',
  'local_display_trigger_level_x_icicle_nova_on_hit_vs_frozen_enemy',
  'local_display_trigger_level_x_stalking_pustule_on_kill',
  'local_display_trigger_level_x_storm_cascade_on_attack',
  'local_display_trigger_tentacle_smash_on_kill_%_chance');

-- Lioneye's Paws and its Replica: the bow-attack triggers already have a rule (trigger_on_bow_attack_%); these rows
-- give their skills a way to go off. Both are approximations: one attack hit with the bow around the aim, not a volley
-- of falling arrows or a field of pods. Art and radius are guesses.
INSERT INTO effect_skills (effect, how, art, radius, note) VALUES
  ('RainOfArrowsTriggered', 'area', 'RainOfArrows', 14, 'Rain of Arrows when you attack with a bow (Lioneye''s Paws): one attack hit around the aim instead of a volley. Art: the gem''s own; radius a guess'),
  ('ToxicRainTriggered', 'area', 'ToxicRainProjectile', 12, 'Toxic Rain when you attack with a bow (Replica Lioneye''s Paws): one attack hit around the aim; the pods and their damage over time are not made. Art and radius are guesses');
UPDATE fx_stats SET handled = 'rule', ref = 'the granted skill''s own trigger (GrantedSkillRules)', why = NULL, fit = 'approximate'
WHERE stat IN ('local_display_trigger_level_x_rain_of_arrows_on_bow_attack', 'local_display_trigger_level_x_toxic_rain_on_bow_attack');
-- Gore Shockwave (Limbsplit, The Cauteriser): its rule and its effect_skills row exist; the harness character has under 150 Strength, so this one was not seen live.
UPDATE fx_stats SET handled = 'rule', ref = 'the granted skill''s own trigger (GrantedSkillRules)', why = NULL, fit = NULL
WHERE stat = 'local_display_trigger_level_x_gore_shockwave_on_melee_hit_with_atleast_150_strength';
