-- The Labyrinth boot enchantments that depend on what happened recently (13 stats on 3 to 5 mods each), built from
-- the modifier table alone: each counts as the plain stats the calculator already reads while its condition holds
-- (been hit recently, killed recently, not killed recently, no critical strike recently). No code read these stats
-- before, so they count with the effect rules on and do nothing with them off. Left for later: the ones that need
-- "cast a spell recently" and "taken a critical strike recently", which nothing records yet.
INSERT INTO fx_modifiers (stat, ord, quantity, how, scale, conditions, counts_as, note) VALUES
('enchantment_boots_added_cold_damage_when_hit_minimum', 0, 'attack_minimum_added_cold_damage', 'flat', 1, '[{"if":"recently","event":"been_hit"}]', 'attack_minimum_added_cold_damage', NULL),
('enchantment_boots_added_cold_damage_when_hit_minimum', 1, 'spell_minimum_added_cold_damage', 'flat', 1, '[{"if":"recently","event":"been_hit"}]', 'spell_minimum_added_cold_damage', NULL),
('enchantment_boots_minimum_added_fire_damage_on_kill_4s', 0, 'attack_minimum_added_fire_damage', 'flat', 1, '[{"if":"recently","event":"killed_enemy"}]', 'attack_minimum_added_fire_damage', NULL),
('enchantment_boots_minimum_added_fire_damage_on_kill_4s', 1, 'spell_minimum_added_fire_damage', 'flat', 1, '[{"if":"recently","event":"killed_enemy"}]', 'spell_minimum_added_fire_damage', NULL),
('enchantment_boots_minimum_added_lightning_damage_when_you_havent_killed_for_4_seconds', 0, 'attack_minimum_added_lightning_damage', 'flat', 1, '[{"if":"recently","event":"killed_enemy","not":true}]', 'attack_minimum_added_lightning_damage', NULL),
('enchantment_boots_minimum_added_lightning_damage_when_you_havent_killed_for_4_seconds', 1, 'spell_minimum_added_lightning_damage', 'flat', 1, '[{"if":"recently","event":"killed_enemy","not":true}]', 'spell_minimum_added_lightning_damage', NULL),
('enchantment_boots_added_cold_damage_when_hit_maximum', 0, 'attack_maximum_added_cold_damage', 'flat', 1, '[{"if":"recently","event":"been_hit"}]', 'attack_maximum_added_cold_damage', NULL),
('enchantment_boots_added_cold_damage_when_hit_maximum', 1, 'spell_maximum_added_cold_damage', 'flat', 1, '[{"if":"recently","event":"been_hit"}]', 'spell_maximum_added_cold_damage', NULL),
('enchantment_boots_maximum_added_fire_damage_on_kill_4s', 0, 'attack_maximum_added_fire_damage', 'flat', 1, '[{"if":"recently","event":"killed_enemy"}]', 'attack_maximum_added_fire_damage', NULL),
('enchantment_boots_maximum_added_fire_damage_on_kill_4s', 1, 'spell_maximum_added_fire_damage', 'flat', 1, '[{"if":"recently","event":"killed_enemy"}]', 'spell_maximum_added_fire_damage', NULL),
('enchantment_boots_maximum_added_lightning_damage_when_you_havent_killed_for_4_seconds', 0, 'attack_maximum_added_lightning_damage', 'flat', 1, '[{"if":"recently","event":"killed_enemy","not":true}]', 'attack_maximum_added_lightning_damage', NULL),
('enchantment_boots_maximum_added_lightning_damage_when_you_havent_killed_for_4_seconds', 1, 'spell_maximum_added_lightning_damage', 'flat', 1, '[{"if":"recently","event":"killed_enemy","not":true}]', 'spell_maximum_added_lightning_damage', NULL),
('enchantment_boots_attack_and_cast_speed_+%_for_4_seconds_on_kill', 0, 'attack_speed_+%', 'flat', 1, '[{"if":"recently","event":"killed_enemy"}]', 'attack_speed_+%', NULL),
('enchantment_boots_attack_and_cast_speed_+%_for_4_seconds_on_kill', 1, 'base_cast_speed_+%', 'flat', 1, '[{"if":"recently","event":"killed_enemy"}]', 'base_cast_speed_+%', NULL),
('enchantment_boots_damage_penetrates_elemental_resistance_%_while_you_havent_killed_for_4_seconds', 0, 'reduce_enemy_elemental_resistance_%', 'flat', 1, '[{"if":"recently","event":"killed_enemy","not":true}]', 'reduce_enemy_elemental_resistance_%', NULL),
('enchantment_boots_life_leech_on_kill_permyriad', 0, 'life_leech_from_any_damage_permyriad', 'flat', 1, '[{"if":"recently","event":"killed_enemy"}]', 'life_leech_from_any_damage_permyriad', NULL),
('enchantment_boots_life_regen_per_minute_%_for_4_seconds_when_hit', 0, 'life_regeneration_rate_per_minute_%', 'flat', 1, '[{"if":"recently","event":"been_hit"}]', 'life_regeneration_rate_per_minute_%', NULL),
('enchantment_boots_mana_costs_when_hit_+%', 0, 'base_mana_cost_-%', 'flat', -1, '[{"if":"recently","event":"been_hit"}]', 'base_mana_cost_-%', 'Increased cost counts against the reduced-cost stat, so its sign is turned.'),
('enchantment_boots_movement_speed_+%_when_not_hit_for_4_seconds', 0, 'base_movement_velocity_+%', 'flat', 1, '[{"if":"recently","event":"been_hit","not":true}]', 'base_movement_velocity_+%', NULL),
('enchantment_boots_status_ailment_chance_+%_when_havent_crit_for_4_seconds', 0, 'base_chance_to_freeze_%', 'flat', 1, '[{"if":"recently","event":"crit","not":true}]', 'base_chance_to_freeze_%', NULL),
('enchantment_boots_status_ailment_chance_+%_when_havent_crit_for_4_seconds', 1, 'base_chance_to_shock_%', 'flat', 1, '[{"if":"recently","event":"crit","not":true}]', 'base_chance_to_shock_%', NULL),
('enchantment_boots_status_ailment_chance_+%_when_havent_crit_for_4_seconds', 2, 'base_chance_to_ignite_%', 'flat', 1, '[{"if":"recently","event":"crit","not":true}]', 'base_chance_to_ignite_%', NULL),
('enchantment_boots_stun_avoid_%_on_kill', 0, 'base_avoid_stun_%', 'flat', 1, '[{"if":"recently","event":"killed_enemy"}]', 'base_avoid_stun_%', NULL)
ON CONFLICT(stat, ord) DO NOTHING;
INSERT INTO fx_stats (stat, kind, handled, ref) VALUES
('enchantment_boots_added_cold_damage_when_hit_maximum', 'conditional number', 'modifier', 'attack_maximum_added_cold_damage flat, by its condition'),
('enchantment_boots_added_cold_damage_when_hit_minimum', 'conditional number', 'modifier', 'attack_minimum_added_cold_damage flat, by its condition'),
('enchantment_boots_attack_and_cast_speed_+%_for_4_seconds_on_kill', 'conditional number', 'modifier', 'attack_speed_+% flat, by its condition'),
('enchantment_boots_damage_penetrates_elemental_resistance_%_while_you_havent_killed_for_4_seconds', 'conditional number', 'modifier', 'reduce_enemy_elemental_resistance_% flat, by its condition'),
('enchantment_boots_life_leech_on_kill_permyriad', 'conditional number', 'modifier', 'life_leech_from_any_damage_permyriad flat, by its condition'),
('enchantment_boots_life_regen_per_minute_%_for_4_seconds_when_hit', 'conditional number', 'modifier', 'life_regeneration_rate_per_minute_% flat, by its condition'),
('enchantment_boots_mana_costs_when_hit_+%', 'conditional number', 'modifier', 'base_mana_cost_-% flat, by its condition'),
('enchantment_boots_maximum_added_fire_damage_on_kill_4s', 'conditional number', 'modifier', 'attack_maximum_added_fire_damage flat, by its condition'),
('enchantment_boots_maximum_added_lightning_damage_when_you_havent_killed_for_4_seconds', 'conditional number', 'modifier', 'attack_maximum_added_lightning_damage flat, by its condition'),
('enchantment_boots_minimum_added_fire_damage_on_kill_4s', 'conditional number', 'modifier', 'attack_minimum_added_fire_damage flat, by its condition'),
('enchantment_boots_minimum_added_lightning_damage_when_you_havent_killed_for_4_seconds', 'conditional number', 'modifier', 'attack_minimum_added_lightning_damage flat, by its condition'),
('enchantment_boots_movement_speed_+%_when_not_hit_for_4_seconds', 'conditional number', 'modifier', 'base_movement_velocity_+% flat, by its condition'),
('enchantment_boots_status_ailment_chance_+%_when_havent_crit_for_4_seconds', 'conditional number', 'modifier', 'base_chance_to_freeze_% flat, by its condition'),
('enchantment_boots_stun_avoid_%_on_kill', 'conditional number', 'modifier', 'base_avoid_stun_% flat, by its condition')
ON CONFLICT(stat) DO UPDATE SET kind = 'conditional number', handled = 'modifier', ref = excluded.ref;
UPDATE fx_stats SET why = 'Nothing records that a spell was cast recently.', fit = 'A modifier row counting as mana_regeneration_rate_+% under a recently condition, once the event is recorded.' WHERE stat = 'enchantment_boots_mana_regeneration_rate_+%_if_cast_spell_recently';
UPDATE fx_stats SET why = 'Nothing records that a critical strike was taken recently.', fit = 'Modifier rows counting as added chaos damage to attacks and spells under a recently condition, once the event is recorded (the when_hit event knows a critical strike).' WHERE stat LIKE 'enchantment_boots_m%imum_added_chaos_damage_for_4_seconds_when_crit_4s';
