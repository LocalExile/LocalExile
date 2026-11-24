-- More stats that depend on what happened recently, built from the modifier table alone, as the boot enchantments
-- were: each counts as stats the calculator already reads while its condition holds. None was read by any code.
INSERT INTO fx_modifiers (stat, ord, quantity, how, conditions, counts_as) VALUES
('movement_velocity_while_not_hit_+%', 0, 'base_movement_velocity_+%', 'flat', '[{"if":"recently","event":"been_hit","not":true}]', 'base_movement_velocity_+%'),
('enchantment_critical_strike_chance_+%_if_you_havent_crit_for_4_seconds', 0, 'critical_strike_chance_+%', 'flat', '[{"if":"recently","event":"crit","not":true}]', 'critical_strike_chance_+%'),
('damage_penetrates_%_elemental_resistance_if_enemy_not_killed_recently', 0, 'reduce_enemy_elemental_resistance_%', 'flat', '[{"if":"recently","event":"killed_enemy","not":true}]', 'reduce_enemy_elemental_resistance_%'),
('attack_and_cast_speed_+%_if_corpse_consumed_recently', 0, 'attack_speed_+%', 'flat', '[{"if":"recently","event":"consumed_corpse"}]', 'attack_speed_+%'),
('attack_and_cast_speed_+%_if_corpse_consumed_recently', 1, 'base_cast_speed_+%', 'flat', '[{"if":"recently","event":"consumed_corpse"}]', 'base_cast_speed_+%'),
('minimum_added_chaos_damage_if_have_crit_recently', 0, 'attack_minimum_added_chaos_damage', 'flat', '[{"if":"recently","event":"crit"}]', 'attack_minimum_added_chaos_damage'),
('minimum_added_chaos_damage_if_have_crit_recently', 1, 'spell_minimum_added_chaos_damage', 'flat', '[{"if":"recently","event":"crit"}]', 'spell_minimum_added_chaos_damage'),
('minimum_added_lightning_damage_if_have_crit_recently', 0, 'attack_minimum_added_lightning_damage', 'flat', '[{"if":"recently","event":"crit"}]', 'attack_minimum_added_lightning_damage'),
('minimum_added_lightning_damage_if_have_crit_recently', 1, 'spell_minimum_added_lightning_damage', 'flat', '[{"if":"recently","event":"crit"}]', 'spell_minimum_added_lightning_damage'),
('maximum_added_chaos_damage_if_have_crit_recently', 0, 'attack_maximum_added_chaos_damage', 'flat', '[{"if":"recently","event":"crit"}]', 'attack_maximum_added_chaos_damage'),
('maximum_added_chaos_damage_if_have_crit_recently', 1, 'spell_maximum_added_chaos_damage', 'flat', '[{"if":"recently","event":"crit"}]', 'spell_maximum_added_chaos_damage'),
('maximum_added_lightning_damage_if_have_crit_recently', 0, 'attack_maximum_added_lightning_damage', 'flat', '[{"if":"recently","event":"crit"}]', 'attack_maximum_added_lightning_damage'),
('maximum_added_lightning_damage_if_have_crit_recently', 1, 'spell_maximum_added_lightning_damage', 'flat', '[{"if":"recently","event":"crit"}]', 'spell_maximum_added_lightning_damage')
ON CONFLICT(stat, ord) DO NOTHING;
INSERT INTO fx_stats (stat, kind, handled, ref) VALUES
('attack_and_cast_speed_+%_if_corpse_consumed_recently', 'conditional number', 'modifier', 'attack_speed_+% flat, by its condition'),
('damage_penetrates_%_elemental_resistance_if_enemy_not_killed_recently', 'conditional number', 'modifier', 'reduce_enemy_elemental_resistance_% flat, by its condition'),
('enchantment_critical_strike_chance_+%_if_you_havent_crit_for_4_seconds', 'conditional number', 'modifier', 'critical_strike_chance_+% flat, by its condition'),
('maximum_added_chaos_damage_if_have_crit_recently', 'conditional number', 'modifier', 'attack_maximum_added_chaos_damage flat, by its condition'),
('maximum_added_lightning_damage_if_have_crit_recently', 'conditional number', 'modifier', 'attack_maximum_added_lightning_damage flat, by its condition'),
('minimum_added_chaos_damage_if_have_crit_recently', 'conditional number', 'modifier', 'attack_minimum_added_chaos_damage flat, by its condition'),
('minimum_added_lightning_damage_if_have_crit_recently', 'conditional number', 'modifier', 'attack_minimum_added_lightning_damage flat, by its condition'),
('movement_velocity_while_not_hit_+%', 'conditional number', 'modifier', 'base_movement_velocity_+% flat, by its condition')
ON CONFLICT(stat) DO UPDATE SET kind = 'conditional number', handled = 'modifier', ref = excluded.ref;
