-- A batch of unique lines done with rows: aliases, item-local rows and rules of existing parts; and marks for four
-- lines built in code.

-- "Gain #% of Weapon Physical Damage as Extra Damage of a random Element" (Doomfletch, Doomfletch's Prism, Starcaller,
-- Voidforge): a third to each element instead of one element per hit. The same damage on average; the panel is steady.
INSERT INTO fx_aliases (stat, ord, counts_as, scale, note) VALUES
('weapon_physical_damage_%_to_add_as_random_element', 0, 'physical_damage_%_to_add_as_fire', 0.3333, 'A third to each element instead of a random one per hit; counts for all physical damage, not the weapon''s alone'),
('weapon_physical_damage_%_to_add_as_random_element', 1, 'physical_damage_%_to_add_as_cold', 0.3333, NULL),
('weapon_physical_damage_%_to_add_as_random_element', 2, 'physical_damage_%_to_add_as_lightning', 0.3333, NULL),
('unique_cold_damage_can_also_ignite', 0, 'cold_damage_can_ignite', 1, 'Your Cold Damage can Ignite (Hrimburn)'),
('local_no_critical_strike_multiplier', 0, 'no_critical_strike_multiplier', 1, 'Critical Strikes with this Weapon do not deal extra Damage (The Golden Charlatan): counted for every hit, not the weapon''s alone');
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_aliases', why = NULL, fit = 'approximate'
WHERE stat IN ('weapon_physical_damage_%_to_add_as_random_element', 'local_no_critical_strike_multiplier');
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_aliases', why = NULL, fit = NULL
WHERE stat = 'unique_cold_damage_can_also_ignite';

-- Lines that belong to the item.
INSERT INTO fx_item_local (stat, ord, slot, counts_as, scale, note) VALUES
('local_physical_damage_%_to_convert_to_a_random_element', 0, 'any', 'base_physical_damage_%_to_convert_to_fire', 0.3333, 'Fate of the Vaal, Story of the Vaal: a third to each element instead of a random one per hit; counts for the character''s physical damage'),
('local_physical_damage_%_to_convert_to_a_random_element', 1, 'any', 'base_physical_damage_%_to_convert_to_cold', 0.3333, NULL),
('local_physical_damage_%_to_convert_to_a_random_element', 2, 'any', 'base_physical_damage_%_to_convert_to_lightning', 0.3333, NULL),
('never_block', 0, 'any', 'cannot_block_attacks', 1, 'Cannot Block (The Eternal Apple)'),
('never_block', 1, 'any', 'cannot_block_spells', 1, NULL),
('local_maim_on_hit', 0, 'any', 'maim_on_hit_%', 100, 'Attacks with this Weapon Maim on hit (Innsbury Edge): counted for every hit of the character'),
('local_withered_on_hit_for_2_seconds_%_chance', 0, 'any', 'withered_on_hit_for_2_seconds_%_chance', 1, 'Replica Innsbury Edge: counted for every hit of the character');
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_item_local', why = NULL, fit = 'approximate'
WHERE stat IN ('local_physical_damage_%_to_convert_to_a_random_element', 'local_maim_on_hit', 'local_withered_on_hit_for_2_seconds_%_chance');
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_item_local', why = NULL, fit = NULL
WHERE stat = 'never_block';

-- Rules of existing parts.
INSERT INTO fx_rules (id, book, title, replaces, status, origin, note) VALUES
('onslaught_on_vaal_skill_use', 'items', 'You gain Onslaught for X seconds on using a Vaal Skill', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before. Vaal Caress.'),
('phasing_on_vaal_skill_use', 'items', 'You gain Phasing for X seconds on using a Vaal Skill', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before. Allure.'),
('unholy_might_on_crit', 'items', 'Gain Unholy Might for 4 seconds on Critical Strike', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before. Death''s Hand. The buff''s numbers are the ones the other Unholy Might rules use.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('onslaught_on_vaal_skill_use', 0, 'stat', 'onslaught_on_vaal_skill_use_duration_ms'),
('phasing_on_vaal_skill_use', 0, 'stat', 'phase_on_vaal_skill_use_duration_ms'),
('unholy_might_on_crit', 0, 'stat', 'gain_unholy_might_for_4_seconds_on_crit');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('onslaught_on_vaal_skill_use', 0, 'skill_use', '{}'),
('phasing_on_vaal_skill_use', 0, 'skill_use', '{}'),
('unholy_might_on_crit', 0, 'hit', '{}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('onslaught_on_vaal_skill_use', 0, 0, 'skill_type', 0, '{"is":"vaal"}'),
('phasing_on_vaal_skill_use', 0, 0, 'skill_type', 0, '{"is":"vaal"}'),
('unholy_might_on_crit', 0, 0, 'critical', 0, '{}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('onslaught_on_vaal_skill_use', 0, 'buff', '{"buff":"onslaught"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"from":"roll","scale":0.001}}]'),
('phasing_on_vaal_skill_use', 0, 'buff', '{"buff":"phasing"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"from":"roll","scale":0.001}}]'),
('unholy_might_on_crit', 0, 'buff', '{"buff":"old_unholy_might","values":[30]}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]');
UPDATE fx_stats SET handled = 'rule', ref = 'onslaught_on_vaal_skill_use', why = NULL, fit = NULL WHERE stat = 'onslaught_on_vaal_skill_use_duration_ms';
UPDATE fx_stats SET handled = 'rule', ref = 'phasing_on_vaal_skill_use', why = NULL, fit = NULL WHERE stat = 'phase_on_vaal_skill_use_duration_ms';
UPDATE fx_stats SET handled = 'rule', ref = 'unholy_might_on_crit', why = NULL, fit = 'approximate' WHERE stat = 'gain_unholy_might_for_4_seconds_on_crit';

-- Built in code.
UPDATE fx_stats SET handled = 'code', ref = 'PlayerStatSteps (BlockDamage step)', why = NULL, fit = NULL
WHERE stat = 'spell_damage_+%_per_5%_block_chance';
UPDATE fx_stats SET handled = 'code', ref = 'Progression (after ItemStatAliases)', why = NULL, fit = 'approximate'
WHERE stat = 'unique_helmet_cast_speed_+%_applies_to_attack_speed_at_%_of_original_value';
UPDATE fx_stats SET handled = 'code', ref = 'MonsterIgnites.IgniteOnHitLocked', why = NULL, fit = NULL
WHERE stat = 'recover_X_life_on_enemy_ignited';
UPDATE fx_stats SET handled = 'code', ref = 'Monsters.cs (the incoming hit)', why = NULL, fit = NULL
WHERE stat = 'recover_X_life_on_suppressing_spell';
UPDATE fx_stats SET handled = 'code', ref = 'PlayerStatSteps (BlockDamage step)', why = NULL, fit = NULL
WHERE stat IN ('elemental_damage_+%_per_lightning_cold_fire_resistance_above_75', 'elemental_damage_+%_per_missing_lightning_cold_fire_resistance');
UPDATE fx_stats SET handled = 'code', ref = 'MonsterAilments.AilmentsOnHitLocked', why = NULL, fit = NULL
WHERE stat = 'unique_add_power_charge_on_melee_knockback_%';
UPDATE fx_stats SET handled = 'code', ref = 'MonsterIgnites.IgniteOnHitLocked', why = NULL, fit = NULL
WHERE stat = 'recover_%_maximum_life_on_enemy_ignited';

-- "Gain # Life per Cursed Enemy Hit with Attacks" (Gluttony).
INSERT INTO fx_rules (id, book, title, replaces, status, origin, note) VALUES
('life_on_attack_hit_vs_cursed', 'items', 'Gain Life per Cursed Enemy Hit with Attacks', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before. Gluttony.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES ('life_on_attack_hit_vs_cursed', 0, 'stat', 'life_gained_on_attack_hit_vs_cursed_enemies');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES ('life_on_attack_hit_vs_cursed', 0, 'hit', '{}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('life_on_attack_hit_vs_cursed', 0, 0, 'skill_type', 0, '{"is":"attack"}'),
('life_on_attack_hit_vs_cursed', 0, 1, 'enemy_state', 0, '{"is":"cursed"}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('life_on_attack_hit_vs_cursed', 0, 'recover', '{"pool":"life"}', 'self', '{}', '{"from":"roll"}', '[]');
UPDATE fx_stats SET handled = 'rule', ref = 'life_on_attack_hit_vs_cursed', why = NULL, fit = NULL WHERE stat = 'life_gained_on_attack_hit_vs_cursed_enemies';
