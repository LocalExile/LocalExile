-- Area modifiers on monsters, built from rows alone: each gives the area's monsters a stat the monster code already
-- acts on (the row's quantity; target enemies_area), as the map modifier code does for the pairs it lists itself.
-- 16 modifiers no code read before: monsters' area of effect, life as extra energy shield, impale, Onslaught, always
-- critical strikes, poison and bleeding on hit, spell block and suppression, cannot be taunted, and others. They
-- count while the effect rules are on.
UPDATE fx_targets SET built = 1, note = note || ' Built for modifier rows of an area modifier: the area''s monsters get the row''s stat.' WHERE id = 'enemies_area';
INSERT INTO fx_modifiers (stat, ord, quantity, how, scale, target, note) VALUES
('map_monsters_area_of_effect_+%', 0, 'base_skill_area_of_effect_+%', 'flat', 1, 'enemies_area', NULL),
('map_monsters_maximum_life_%_to_add_to_maximum_energy_shield', 0, 'maximum_life_%_to_add_as_maximum_energy_shield', 'flat', 1, 'enemies_area', NULL),
('map_monsters_chance_to_impale_%', 0, 'attacks_impale_on_hit_%_chance', 'flat', 1, 'enemies_area', NULL),
('map_monsters_avoid_poison_bleed_impale_%', 0, 'avoid_poison_%', 'flat', 1, 'enemies_area', 'Bleeding is not covered: the monster code reads no chance to avoid it.'),
('map_monsters_avoid_poison_bleed_impale_%', 1, 'avoid_impale_%', 'flat', 1, 'enemies_area', 'Bleeding is not covered: the monster code reads no chance to avoid it.'),
('map_monsters_have_onslaught', 0, 'attack_speed_+%', 'flat', 20, 'enemies_area', 'Onslaught is 20% increased attack, cast and movement speed.'),
('map_monsters_have_onslaught', 1, 'base_cast_speed_+%', 'flat', 20, 'enemies_area', 'Onslaught is 20% increased attack, cast and movement speed.'),
('map_monsters_have_onslaught', 2, 'base_movement_velocity_+%', 'flat', 20, 'enemies_area', 'Onslaught is 20% increased attack, cast and movement speed.'),
('map_monsters_cannot_be_taunted', 0, 'cannot_be_taunted', 'flat', 1, 'enemies_area', NULL),
('map_monsters_spell_damage_%_suppressed', 0, 'base_spell_damage_%_suppressed', 'flat', 1, 'enemies_area', NULL),
('map_monsters_base_spell_block_%', 0, 'base_spell_block_%', 'flat', 1, 'enemies_area', NULL),
('map_monster_non_damaging_ailment_effect_+%_on_self', 0, 'non_damaging_ailment_effect_+%_on_self', 'flat', 1, 'enemies_area', NULL),
('map_monsters_global_poison_on_hit', 0, 'global_poison_on_hit', 'flat', 1, 'enemies_area', NULL),
('map_monsters_global_bleed_on_hit', 0, 'global_bleed_on_hit', 'flat', 1, 'enemies_area', NULL),
('map_monsters_always_crit', 0, 'always_crit', 'flat', 1, 'enemies_area', NULL),
('map_monsters_action_speed_-%', 0, 'action_speed_-%', 'flat', 1, 'enemies_area', NULL),
('map_monsters_avoid_ignite_%', 0, 'avoid_ignite_%', 'flat', 1, 'enemies_area', NULL),
('map_monster_movement_speed_cannot_be_reduced_below_base', 0, 'movement_speed_cannot_be_reduced_below_base', 'flat', 1, 'enemies_area', NULL),
('map_monster_curse_effect_on_self_+%', 0, 'curse_effect_on_self_+%', 'flat', 1, 'enemies_area', NULL)
ON CONFLICT(stat, ord) DO NOTHING;
INSERT INTO fx_stats (stat, kind, handled, ref) VALUES
('map_monsters_area_of_effect_+%', 'number', 'modifier', 'base_skill_area_of_effect_+% on the areas monsters'),
('map_monsters_maximum_life_%_to_add_to_maximum_energy_shield', 'number', 'modifier', 'maximum_life_%_to_add_as_maximum_energy_shield on the areas monsters'),
('map_monsters_chance_to_impale_%', 'number', 'modifier', 'attacks_impale_on_hit_%_chance on the areas monsters'),
('map_monsters_avoid_poison_bleed_impale_%', 'number', 'modifier', 'avoid_poison_% on the areas monsters'),
('map_monsters_have_onslaught', 'number', 'modifier', 'attack_speed_+% on the areas monsters'),
('map_monsters_cannot_be_taunted', 'number', 'modifier', 'cannot_be_taunted on the areas monsters'),
('map_monsters_spell_damage_%_suppressed', 'number', 'modifier', 'base_spell_damage_%_suppressed on the areas monsters'),
('map_monsters_base_spell_block_%', 'number', 'modifier', 'base_spell_block_% on the areas monsters'),
('map_monster_non_damaging_ailment_effect_+%_on_self', 'number', 'modifier', 'non_damaging_ailment_effect_+%_on_self on the areas monsters'),
('map_monsters_global_poison_on_hit', 'number', 'modifier', 'global_poison_on_hit on the areas monsters'),
('map_monsters_global_bleed_on_hit', 'number', 'modifier', 'global_bleed_on_hit on the areas monsters'),
('map_monsters_always_crit', 'number', 'modifier', 'always_crit on the areas monsters'),
('map_monsters_action_speed_-%', 'number', 'modifier', 'action_speed_-% on the areas monsters'),
('map_monsters_avoid_ignite_%', 'number', 'modifier', 'avoid_ignite_% on the areas monsters'),
('map_monster_movement_speed_cannot_be_reduced_below_base', 'number', 'modifier', 'movement_speed_cannot_be_reduced_below_base on the areas monsters'),
('map_monster_curse_effect_on_self_+%', 'number', 'modifier', 'curse_effect_on_self_+% on the areas monsters')
ON CONFLICT(stat) DO UPDATE SET handled = 'modifier', ref = excluded.ref;
UPDATE fx_stats SET handled = 'code', code = 'MapModApply.cs' WHERE stat = 'map_monster_skills_chain_X_additional_times' AND handled = 'none';
