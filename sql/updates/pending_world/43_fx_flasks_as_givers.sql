-- Flasks as givers, and the flask-use trigger.
--
-- A flask in the flask bar now carries rules like a worn item does (source kind "item"): its "when used" rules run
-- when that flask is used, and its standing ("active") rules hold only while its effect runs, which is what "during
-- Effect" means. The trigger "flask_use" is the paid use of any flask: a utility flask, or a life, mana or hybrid one.
--
-- Built with it from rows alone: 27 "during Effect" lines no code read (the flask code's own list covers the rest),
-- 4 lines for the character's "when you use a Flask", and 3 "during any Flask Effect" numbers as conditional rows.
-- A line's number is given as rolled: a flask's "increased effect" does not scale these yet. A life or mana flask's
-- recovery is not followed as an effect, so "during Effect" lines hold for utility flasks only.
UPDATE fx_triggers SET built = 1, takes = 'nothing; the condition "flask_kind" tells a utility, life, mana or hybrid flask',
  note = 'The actor used a flask and paid its charges. A rule a flask carries (source kind "item") runs for that flask''s own use; a rule from a character stat runs for every flask.'
WHERE id = 'flask_use';
INSERT INTO fx_conditions (id, takes, built, origin, note) VALUES
('flask_kind', 'is: utility, life, mana, hybrid', 1, 'parts', 'What the used flask is, for a "flask_use" trigger. A hybrid flask is neither a life nor a mana flask.')
ON CONFLICT(id) DO UPDATE SET takes = excluded.takes, built = 1, note = excluded.note;
INSERT INTO fx_rules (id, book, title, replaces, status, origin, note) VALUES
('flask_attack_damage_leeched_as_life', 'flasks', '#% of Attack Damage Leeched as Life during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_physical_attack_damage_leeched_as_life', 'flasks', '#% of Physical Attack Damage Leeched as Life during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_physical_attack_damage_leeched_as_mana', 'flasks', '#% of Physical Attack Damage Leeched as Mana during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_lightning_damage_leeched_as_life', 'flasks', '#% of Lightning Damage Leeched as Life during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_lightning_damage_leeched_as_mana', 'flasks', '#% of Lightning Damage Leeched as Mana during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_item_rarity', 'flasks', '#% increased Rarity of Items found during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_life_regeneration', 'flasks', 'Regenerate #% of Life per second during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_accuracy', 'flasks', '#% increased Accuracy Rating during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_chaos_damage_taken', 'flasks', 'Take # Chaos Damage per Second during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_poison_duration', 'flasks', '#% increased Duration of Poisons you inflict during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_chance_to_poison', 'flasks', '#% chance to Poison on Hit during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_damage_over_time', 'flasks', '#% increased Damage Over Time during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_lightning_penetration', 'flasks', 'Damage Penetrates #% Lightning Resistance during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_added_lightning_attacks_minimum', 'flasks', 'Adds # to # Lightning Damage to Attacks during Effect (the lower number)', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_added_lightning_attacks_maximum', 'flasks', 'Adds # to # Lightning Damage to Attacks during Effect (the higher number)', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_added_lightning_spells_minimum', 'flasks', 'Adds # to # Lightning Damage to Spells during Effect (the lower number)', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_added_lightning_spells_maximum', 'flasks', 'Adds # to # Lightning Damage to Spells during Effect (the higher number)', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_physical_converted_to_lightning', 'flasks', '#% of Physical Damage Converted to Lightning during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_attack_cast_movement_speed', 'flasks', '#% increased Attack, Cast and Movement Speed during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_self_bleed_duration', 'flasks', '#% increased Bleeding Duration on you during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_self_poison_duration', 'flasks', '#% increased Poison Duration on you during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_self_ignite_duration', 'flasks', '#% increased Ignite Duration on you during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_non_damaging_ailment_effect', 'flasks', '#% increased Effect of Non-Damaging Ailments you inflict during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_cannot_be_stunned', 'flasks', 'Cannot be Stunned during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_culling_strike', 'flasks', 'Culling Strike during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_eldritch_battery', 'flasks', 'Eldritch Battery during Effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flask_chaos_does_not_bypass_energy_shield', 'flasks', 'Chaos Damage taken does not bypass Energy Shield during effect', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('onslaught_on_flask_use', 'flasks', '#% chance to gain Onslaught when you use a Flask', NULL, 'built', 'parts', 'Built from parts alone. Onslaught lasts 4 seconds, the length the game gives a chance to gain it; not measured for this line.'),
('recover_life_on_flask_use', 'flasks', 'Recover #% of Life when you use a Flask', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('recover_life_on_mana_flask_use', 'flasks', 'Recover #% of Life when you use a Mana Flask', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('power_charge_on_mana_flask_use', 'flasks', '#% chance to gain a Power Charge when you use a Mana Flask', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('flask_attack_damage_leeched_as_life', 0, 'item', 'local_flask_life_leech_from_attack_damage_permyriad_while_healing'),
('flask_physical_attack_damage_leeched_as_life', 0, 'item', 'local_flask_life_leech_permyriad_while_healing'),
('flask_physical_attack_damage_leeched_as_mana', 0, 'item', 'local_flask_mana_leech_permyriad_while_healing'),
('flask_lightning_damage_leeched_as_life', 0, 'item', 'local_unique_flask_leech_lightning_damage_%_as_life_during_flask_effect'),
('flask_lightning_damage_leeched_as_mana', 0, 'item', 'local_unique_flask_leech_lightning_damage_%_as_mana_during_flask_effect'),
('flask_item_rarity', 0, 'item', 'local_flask_item_found_rarity_+%_during_flask_effect'),
('flask_life_regeneration', 0, 'item', 'local_flask_life_regeneration_per_minute_%_during_flask_effect'),
('flask_accuracy', 0, 'item', 'local_flask_accuracy_rating_+%_during_effect'),
('flask_chaos_damage_taken', 0, 'item', 'local_chaos_damage_taken_per_minute_during_flask_effect'),
('flask_poison_duration', 0, 'item', 'local_poison_duration_+%_during_flask_effect'),
('flask_chance_to_poison', 0, 'item', 'local_chance_to_poison_on_hit_%_during_flask_effect'),
('flask_damage_over_time', 0, 'item', 'local_unique_flask_damage_over_time_+%_during_flask_effect'),
('flask_lightning_penetration', 0, 'item', 'local_unique_flask_lightning_resistance_penetration_%_during_flask_effect'),
('flask_added_lightning_attacks_minimum', 0, 'item', 'local_unique_flask_minimum_added_lightning_damage_to_attacks_during_flask_effect'),
('flask_added_lightning_attacks_maximum', 0, 'item', 'local_unique_flask_maximum_added_lightning_damage_to_attacks_during_flask_effect'),
('flask_added_lightning_spells_minimum', 0, 'item', 'local_unique_flask_minimum_added_lightning_damage_to_spells_during_flask_effect'),
('flask_added_lightning_spells_maximum', 0, 'item', 'local_unique_flask_maximum_added_lightning_damage_to_spells_during_flask_effect'),
('flask_physical_converted_to_lightning', 0, 'item', 'local_unique_flask_physical_damage_%_converted_to_lightning_during_flask_effect'),
('flask_attack_cast_movement_speed', 0, 'item', 'local_attack_cast_movement_speed_+%_during_flask_effect'),
('flask_self_bleed_duration', 0, 'item', 'local_self_bleed_duration_+%_during_flask_effect'),
('flask_self_poison_duration', 0, 'item', 'local_self_poison_duration_+%_during_flask_effect'),
('flask_self_ignite_duration', 0, 'item', 'local_self_ignite_duration_+%_during_flask_effect'),
('flask_non_damaging_ailment_effect', 0, 'item', 'local_flask_non_damaging_ailment_effect_+%_during_flask_effect'),
('flask_cannot_be_stunned', 0, 'item', 'local_flask_cannot_be_stunned_during_flask_effect'),
('flask_culling_strike', 0, 'item', 'local_flask_culling_strike_during_flask_effect'),
('flask_eldritch_battery', 0, 'item', 'local_grant_eldritch_battery_during_flask_effect'),
('flask_chaos_does_not_bypass_energy_shield', 0, 'item', 'local_unique_chaos_damage_does_not_bypass_energy_shield_during_flask_effect'),
('onslaught_on_flask_use', 0, 'stat', 'chance_to_gain_onslaught_on_flask_use_%'),
('recover_life_on_flask_use', 0, 'stat', 'recover_%_maximum_life_on_flask_use'),
('recover_life_on_mana_flask_use', 0, 'stat', 'recover_%_maximum_life_on_mana_flask_use'),
('power_charge_on_mana_flask_use', 0, 'stat', 'gain_power_charge_on_mana_flask_use_%_chance');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('flask_attack_damage_leeched_as_life', 0, 'active', '{}'),
('flask_physical_attack_damage_leeched_as_life', 0, 'active', '{}'),
('flask_physical_attack_damage_leeched_as_mana', 0, 'active', '{}'),
('flask_lightning_damage_leeched_as_life', 0, 'active', '{}'),
('flask_lightning_damage_leeched_as_mana', 0, 'active', '{}'),
('flask_item_rarity', 0, 'active', '{}'),
('flask_life_regeneration', 0, 'active', '{}'),
('flask_accuracy', 0, 'active', '{}'),
('flask_chaos_damage_taken', 0, 'active', '{}'),
('flask_poison_duration', 0, 'active', '{}'),
('flask_chance_to_poison', 0, 'active', '{}'),
('flask_damage_over_time', 0, 'active', '{}'),
('flask_lightning_penetration', 0, 'active', '{}'),
('flask_added_lightning_attacks_minimum', 0, 'active', '{}'),
('flask_added_lightning_attacks_maximum', 0, 'active', '{}'),
('flask_added_lightning_spells_minimum', 0, 'active', '{}'),
('flask_added_lightning_spells_maximum', 0, 'active', '{}'),
('flask_physical_converted_to_lightning', 0, 'active', '{}'),
('flask_attack_cast_movement_speed', 0, 'active', '{}'),
('flask_self_bleed_duration', 0, 'active', '{}'),
('flask_self_poison_duration', 0, 'active', '{}'),
('flask_self_ignite_duration', 0, 'active', '{}'),
('flask_non_damaging_ailment_effect', 0, 'active', '{}'),
('flask_cannot_be_stunned', 0, 'active', '{}'),
('flask_culling_strike', 0, 'active', '{}'),
('flask_eldritch_battery', 0, 'active', '{}'),
('flask_chaos_does_not_bypass_energy_shield', 0, 'active', '{}'),
('onslaught_on_flask_use', 0, 'flask_use', '{}'),
('recover_life_on_flask_use', 0, 'flask_use', '{}'),
('recover_life_on_mana_flask_use', 0, 'flask_use', '{}'),
('power_charge_on_mana_flask_use', 0, 'flask_use', '{}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('onslaught_on_flask_use', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('recover_life_on_mana_flask_use', 0, 0, 'flask_kind', 0, '{"is":"mana"}'),
('power_charge_on_mana_flask_use', 0, 0, 'flask_kind', 0, '{"is":"mana"}'),
('power_charge_on_mana_flask_use', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('flask_attack_damage_leeched_as_life', 0, 'quantity', '{"stats":{"base_life_leech_from_attack_damage_permyriad":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_physical_attack_damage_leeched_as_life', 0, 'quantity', '{"stats":{"life_leech_from_physical_attack_damage_permyriad":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_physical_attack_damage_leeched_as_mana', 0, 'quantity', '{"stats":{"mana_leech_from_physical_attack_damage_permyriad":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_lightning_damage_leeched_as_life', 0, 'quantity', '{"stats":{"base_life_leech_from_lightning_damage_permyriad":100}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_lightning_damage_leeched_as_mana', 0, 'quantity', '{"stats":{"base_mana_leech_from_lightning_damage_permyriad":100}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_item_rarity', 0, 'quantity', '{"stats":{"base_item_found_rarity_+%":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_life_regeneration', 0, 'quantity', '{"stats":{"life_regeneration_rate_per_minute_%":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_accuracy', 0, 'quantity', '{"stats":{"accuracy_rating_+%":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_chaos_damage_taken', 0, 'quantity', '{"stats":{"base_chaos_damage_taken_per_minute":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_poison_duration', 0, 'quantity', '{"stats":{"base_poison_duration_+%":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_chance_to_poison', 0, 'quantity', '{"stats":{"base_chance_to_poison_on_hit_%":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_damage_over_time', 0, 'quantity', '{"stats":{"damage_over_time_+%":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_lightning_penetration', 0, 'quantity', '{"stats":{"base_reduce_enemy_lightning_resistance_%":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_added_lightning_attacks_minimum', 0, 'quantity', '{"stats":{"attack_minimum_added_lightning_damage":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_added_lightning_attacks_maximum', 0, 'quantity', '{"stats":{"attack_maximum_added_lightning_damage":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_added_lightning_spells_minimum', 0, 'quantity', '{"stats":{"spell_minimum_added_lightning_damage":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_added_lightning_spells_maximum', 0, 'quantity', '{"stats":{"spell_maximum_added_lightning_damage":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_physical_converted_to_lightning', 0, 'quantity', '{"stats":{"base_physical_damage_%_to_convert_to_lightning":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_attack_cast_movement_speed', 0, 'quantity', '{"stats":{"attack_speed_+%":1,"base_cast_speed_+%":1,"base_movement_velocity_+%":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_self_bleed_duration', 0, 'quantity', '{"stats":{"self_bleed_duration_+%":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_self_poison_duration', 0, 'quantity', '{"stats":{"self_poison_duration_+%":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_self_ignite_duration', 0, 'quantity', '{"stats":{"base_self_ignite_duration_-%":-1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_non_damaging_ailment_effect', 0, 'quantity', '{"stats":{"non_damaging_ailment_effect_+%":1}}', 'self', '{}', '{"from":"roll","scale":100}', '[{"timer":"source"}]'),
('flask_cannot_be_stunned', 0, 'quantity', '{"stats":{"base_cannot_be_stunned":1}}', 'self', '{}', '{}', '[{"timer":"source"}]'),
('flask_culling_strike', 0, 'quantity', '{"stats":{"kill_enemy_on_hit_if_under_10%_life":1}}', 'self', '{}', '{}', '[{"timer":"source"}]'),
('flask_eldritch_battery', 0, 'quantity', '{"stats":{"keystone_eldritch_battery":1}}', 'self', '{}', '{}', '[{"timer":"source"}]'),
('flask_chaos_does_not_bypass_energy_shield', 0, 'quantity', '{"stats":{"chaos_damage_does_not_bypass_energy_shield":1}}', 'self', '{}', '{}', '[{"timer":"source"}]'),
('onslaught_on_flask_use', 0, 'buff', '{"buff":"onslaught"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('recover_life_on_flask_use', 0, 'recover', '{"pool":"life","of":"maximum"}', 'self', '{}', '{"from":"roll"}', '[]'),
('recover_life_on_mana_flask_use', 0, 'recover', '{"pool":"life","of":"maximum"}', 'self', '{}', '{"from":"roll"}', '[]'),
('power_charge_on_mana_flask_use', 0, 'charge', '{"type":"power"}', 'self', '{}', '{"fixed":1}', '[]');
-- "during any Flask Effect" numbers that counted for nothing: the state the other such rows already use.
INSERT INTO fx_modifiers (stat, ord, quantity, how, scale, conditions, target, enabled, checked, note, counts_as, per) VALUES
('item_rarity_+%_while_using_flask', 0, 'base_item_found_rarity', 'increased', 1.0, '[{"if":"state","is":"while_using_flask"}]', 'self', 1, 0, '{0}% increased Rarity of Items found during any Flask Effect', 'base_item_found_rarity_+%', NULL),
('stun_threshold_reduction_+%_while_using_flask', 0, 'base_stun_threshold_reduction', 'increased', 1.0, '[{"if":"state","is":"while_using_flask"}]', 'self', 1, 0, '{0}% reduced Enemy Stun Threshold during any Flask Effect', 'base_stun_threshold_reduction_+%', NULL),
('energy_shield_delay_during_flask_effect_-%', 0, 'energy_shield_delay_-%', 'flat', 1.0, '[{"if":"state","is":"during_flask_effect"}]', 'self', 1, 0, '{0}% faster start of Energy Shield Recharge during any Flask Effect', 'energy_shield_delay_-%', NULL);
UPDATE fx_stats SET handled = 'modifier', ref = 'counts as base_item_found_rarity_+%', why = NULL, fit = NULL WHERE stat = 'item_rarity_+%_while_using_flask';
UPDATE fx_stats SET handled = 'modifier', ref = 'counts as base_stun_threshold_reduction_+%', why = NULL, fit = NULL WHERE stat = 'stun_threshold_reduction_+%_while_using_flask';
UPDATE fx_stats SET handled = 'modifier', ref = 'counts as energy_shield_delay_-%', why = NULL, fit = NULL WHERE stat = 'energy_shield_delay_during_flask_effect_-%';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_attack_damage_leeched_as_life', why = NULL, fit = NULL WHERE stat = 'local_flask_life_leech_from_attack_damage_permyriad_while_healing';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_physical_attack_damage_leeched_as_life', why = NULL, fit = NULL WHERE stat = 'local_flask_life_leech_permyriad_while_healing';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_physical_attack_damage_leeched_as_mana', why = NULL, fit = NULL WHERE stat = 'local_flask_mana_leech_permyriad_while_healing';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_lightning_damage_leeched_as_life', why = NULL, fit = NULL WHERE stat = 'local_unique_flask_leech_lightning_damage_%_as_life_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_lightning_damage_leeched_as_mana', why = NULL, fit = NULL WHERE stat = 'local_unique_flask_leech_lightning_damage_%_as_mana_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_item_rarity', why = NULL, fit = NULL WHERE stat = 'local_flask_item_found_rarity_+%_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_life_regeneration', why = NULL, fit = NULL WHERE stat = 'local_flask_life_regeneration_per_minute_%_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_accuracy', why = NULL, fit = NULL WHERE stat = 'local_flask_accuracy_rating_+%_during_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_chaos_damage_taken', why = NULL, fit = NULL WHERE stat = 'local_chaos_damage_taken_per_minute_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_poison_duration', why = NULL, fit = NULL WHERE stat = 'local_poison_duration_+%_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_chance_to_poison', why = NULL, fit = NULL WHERE stat = 'local_chance_to_poison_on_hit_%_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_damage_over_time', why = NULL, fit = NULL WHERE stat = 'local_unique_flask_damage_over_time_+%_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_lightning_penetration', why = NULL, fit = NULL WHERE stat = 'local_unique_flask_lightning_resistance_penetration_%_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_added_lightning_attacks_minimum', why = NULL, fit = NULL WHERE stat = 'local_unique_flask_minimum_added_lightning_damage_to_attacks_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_added_lightning_attacks_maximum', why = NULL, fit = NULL WHERE stat = 'local_unique_flask_maximum_added_lightning_damage_to_attacks_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_added_lightning_spells_minimum', why = NULL, fit = NULL WHERE stat = 'local_unique_flask_minimum_added_lightning_damage_to_spells_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_added_lightning_spells_maximum', why = NULL, fit = NULL WHERE stat = 'local_unique_flask_maximum_added_lightning_damage_to_spells_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_physical_converted_to_lightning', why = NULL, fit = NULL WHERE stat = 'local_unique_flask_physical_damage_%_converted_to_lightning_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_attack_cast_movement_speed', why = NULL, fit = NULL WHERE stat = 'local_attack_cast_movement_speed_+%_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_self_bleed_duration', why = NULL, fit = NULL WHERE stat = 'local_self_bleed_duration_+%_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_self_poison_duration', why = NULL, fit = NULL WHERE stat = 'local_self_poison_duration_+%_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_self_ignite_duration', why = NULL, fit = NULL WHERE stat = 'local_self_ignite_duration_+%_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_non_damaging_ailment_effect', why = NULL, fit = NULL WHERE stat = 'local_flask_non_damaging_ailment_effect_+%_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_cannot_be_stunned', why = NULL, fit = NULL WHERE stat = 'local_flask_cannot_be_stunned_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_culling_strike', why = NULL, fit = NULL WHERE stat = 'local_flask_culling_strike_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_eldritch_battery', why = NULL, fit = NULL WHERE stat = 'local_grant_eldritch_battery_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'flask_chaos_does_not_bypass_energy_shield', why = NULL, fit = NULL WHERE stat = 'local_unique_chaos_damage_does_not_bypass_energy_shield_during_flask_effect';
UPDATE fx_stats SET handled = 'rule', ref = 'onslaught_on_flask_use', why = NULL, fit = NULL WHERE stat = 'chance_to_gain_onslaught_on_flask_use_%';
UPDATE fx_stats SET handled = 'rule', ref = 'recover_life_on_flask_use', why = NULL, fit = NULL WHERE stat = 'recover_%_maximum_life_on_flask_use';
UPDATE fx_stats SET handled = 'rule', ref = 'recover_life_on_mana_flask_use', why = NULL, fit = NULL WHERE stat = 'recover_%_maximum_life_on_mana_flask_use';
UPDATE fx_stats SET handled = 'rule', ref = 'power_charge_on_mana_flask_use', why = NULL, fit = NULL WHERE stat = 'gain_power_charge_on_mana_flask_use_%_chance';
-- Flask stats the flask code already reads, shown as unbuilt until now.
UPDATE fx_stats SET handled = 'code', code = 'UtilityFlasks.cs', why = NULL, fit = NULL WHERE stat IN ('local_flask_attack_speed_+%_while_healing', 'local_flask_cast_speed_+%_while_healing', 'local_flask_chill_or_freeze_immunity_if_chilled_or_frozen', 'local_flask_chill_or_freeze_immunity_if_chilled_or_frozen_s', 'local_flask_chilled_ground_on_flask_use_radius', 'local_flask_poison_immunity_if_poisoned', 'local_flask_shock_immunity_if_shocked', 'local_flask_shock_immunity_if_shocked_s', 'local_unique_flask_start_energy_shield_recharge');
