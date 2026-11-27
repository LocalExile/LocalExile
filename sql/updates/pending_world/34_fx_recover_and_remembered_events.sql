-- Two new parts, and the stats they unlock; none of these stats was read by any code.
-- "recover": life, mana or energy shield gained or lost at once, flat or as a percent of the maximum (8 stats).
-- "raise": a rule records that something happened (a spell cast, a movement or travel skill used, a critical strike
-- taken, an enemy stunned), and the modifier table's "recently" conditions can ask for it (10 stats). A stat whose
-- rule records the event its own condition waits for keeps its id whatever the condition says, so the rule stays on.
UPDATE fx_actions SET built = 1, takes = 'pool: life, mana, energy shield; does: gain (the default) or lose; of: maximum, to read the value as a percent of the pool''s maximum',
    note = 'Gives the actor life, mana or energy shield at once, or takes it. A gain goes through the game''s own instant recovery; a loss is not damage and leaves 1 life.'
WHERE id = 'recover';
UPDATE fx_actions SET built = 1, takes = 'event: a name for what happened; the duration timer says how long it counts as recent (4 seconds unless given)',
    note = 'Records that something happened to the actor, so a modifier row''s "recently" condition can ask for it by the same name. The rule''s sources are the stats that ask.'
WHERE id = 'raise';
INSERT INTO fx_rules (id, book, title, replaces, status, origin, note) VALUES
('life_on_stun', 'recovery', 'Gain Life when you Stun an Enemy', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('lose_life_on_crit', 'recovery', 'Lose a percent of Life when you deal a Critical Strike', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('lose_es_on_crit', 'recovery', 'Lose a percent of Energy Shield when you deal a Critical Strike', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('lose_es_on_block', 'recovery', 'Lose a percent of your Energy Shield when you Block', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('lose_mana_on_attack_skill', 'recovery', 'Lose a percent of Mana when you use an Attack Skill', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('lose_mana_on_skill', 'recovery', 'Lose Mana when you use a Skill', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('lose_es_on_kill', 'recovery', 'Lose a percent of Energy Shield on Kill', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('lose_life_on_kill', 'recovery', 'Lose a percent of Life on Kill', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('remember_cast_spell', 'events', 'Remembers for 4 seconds that you cast a Spell', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('remember_used_movement_skill', 'events', 'Remembers for 4 seconds that you used a Movement Skill', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('remember_used_travel_skill', 'events', 'Remembers for 4 seconds that you used a Travel Skill', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('remember_taken_crit', 'events', 'Remembers for 4 seconds that you took a Critical Strike', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('remember_stunned_enemy', 'events', 'Remembers for 4 seconds that you Stunned an Enemy', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('life_on_stun', 0, 'stat', 'gain_X_life_on_stun'),
('lose_life_on_crit', 0, 'stat', 'lose_%_of_life_on_crit'),
('lose_es_on_crit', 0, 'stat', 'lose_%_of_es_on_crit'),
('lose_es_on_block', 0, 'stat', 'energy_shield_%_to_lose_on_block'),
('lose_mana_on_attack_skill', 0, 'stat', 'lose_%_of_mana_when_you_use_an_attack_skill'),
('lose_mana_on_skill', 0, 'stat', 'lose_x_mana_when_you_use_skill'),
('lose_es_on_kill', 0, 'stat', 'maximum_energy_shield_%_lost_on_kill'),
('lose_life_on_kill', 0, 'stat', 'maximum_life_%_lost_on_kill'),
('remember_cast_spell', 0, 'stat', 'enchantment_boots_mana_regeneration_rate_+%_if_cast_spell_recently'),
('remember_used_movement_skill', 0, 'stat', 'attack_and_cast_speed_+%_for_4_seconds_on_movement_skill_use'),
('remember_used_movement_skill', 1, 'stat', 'mana_regeneration_+%_for_4_seconds_on_movement_skill_use'),
('remember_used_travel_skill', 0, 'stat', 'damage_+%_if_used_travel_skill_recently'),
('remember_taken_crit', 0, 'stat', 'enchantment_boots_minimum_added_chaos_damage_for_4_seconds_when_crit_4s'),
('remember_taken_crit', 1, 'stat', 'enchantment_boots_maximum_added_chaos_damage_for_4_seconds_when_crit_4s'),
('remember_taken_crit', 2, 'stat', 'extra_damage_taken_from_crit_-%_if_taken_critical_strike_recently'),
('remember_stunned_enemy', 0, 'stat', 'area_of_effect_+%_if_have_stunned_an_enemy_recently'),
('remember_stunned_enemy', 1, 'stat', 'chance_to_deal_double_damage_%_if_have_stunned_an_enemy_recently');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('life_on_stun', 0, 'stun', '{}'),
('lose_life_on_crit', 0, 'hit', '{}'),
('lose_es_on_crit', 0, 'hit', '{}'),
('lose_es_on_block', 0, 'block', '{}'),
('lose_mana_on_attack_skill', 0, 'skill_use', '{}'),
('lose_mana_on_skill', 0, 'skill_use', '{}'),
('lose_es_on_kill', 0, 'kill', '{}'),
('lose_life_on_kill', 0, 'kill', '{}'),
('remember_cast_spell', 0, 'skill_use', '{}'),
('remember_used_movement_skill', 0, 'skill_use', '{}'),
('remember_used_travel_skill', 0, 'skill_use', '{}'),
('remember_taken_crit', 0, 'when_hit', '{}'),
('remember_stunned_enemy', 0, 'stun', '{}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('lose_life_on_crit', 0, 0, 'critical', 0, '{}'),
('lose_es_on_crit', 0, 0, 'critical', 0, '{}'),
('lose_mana_on_attack_skill', 0, 0, 'skill_type', 0, '{"is":"attack"}'),
('remember_cast_spell', 0, 0, 'skill_type', 0, '{"is":"spell"}'),
('remember_used_movement_skill', 0, 0, 'skill_type', 0, '{"is":"movement"}'),
('remember_used_travel_skill', 0, 0, 'skill_type', 0, '{"is":"travel"}'),
('remember_taken_crit', 0, 0, 'critical', 0, '{}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('life_on_stun', 0, 'recover', '{"pool":"life"}', 'self', '{}', '{"from":"roll"}', '[]'),
('lose_life_on_crit', 0, 'recover', '{"pool":"life","does":"lose","of":"maximum"}', 'self', '{}', '{"from":"roll"}', '[]'),
('lose_es_on_crit', 0, 'recover', '{"pool":"energy shield","does":"lose","of":"maximum"}', 'self', '{}', '{"from":"roll"}', '[]'),
('lose_es_on_block', 0, 'recover', '{"pool":"energy shield","does":"lose","of":"maximum"}', 'self', '{}', '{"from":"roll"}', '[]'),
('lose_mana_on_attack_skill', 0, 'recover', '{"pool":"mana","does":"lose","of":"maximum"}', 'self', '{}', '{"from":"roll"}', '[]'),
('lose_mana_on_skill', 0, 'recover', '{"pool":"mana","does":"lose"}', 'self', '{}', '{"from":"roll"}', '[]'),
('lose_es_on_kill', 0, 'recover', '{"pool":"energy shield","does":"lose","of":"maximum"}', 'self', '{}', '{"from":"roll"}', '[]'),
('lose_life_on_kill', 0, 'recover', '{"pool":"life","does":"lose","of":"maximum"}', 'self', '{}', '{"from":"roll"}', '[]'),
('remember_cast_spell', 0, 'raise', '{"event":"cast_spell"}', 'self', '{}', '{}', '[]'),
('remember_used_movement_skill', 0, 'raise', '{"event":"used_movement_skill"}', 'self', '{}', '{}', '[]'),
('remember_used_travel_skill', 0, 'raise', '{"event":"used_travel_skill"}', 'self', '{}', '{}', '[]'),
('remember_taken_crit', 0, 'raise', '{"event":"taken_crit"}', 'self', '{}', '{}', '[]'),
('remember_stunned_enemy', 0, 'raise', '{"event":"stunned_enemy"}', 'self', '{}', '{}', '[]');
INSERT INTO fx_modifiers (stat, ord, quantity, how, conditions, counts_as) VALUES
('enchantment_boots_mana_regeneration_rate_+%_if_cast_spell_recently', 0, 'mana_regeneration_rate_+%', 'flat', '[{"if":"recently","event":"cast_spell"}]', 'mana_regeneration_rate_+%'),
('attack_and_cast_speed_+%_for_4_seconds_on_movement_skill_use', 0, 'attack_speed_+%', 'flat', '[{"if":"recently","event":"used_movement_skill"}]', 'attack_speed_+%'),
('attack_and_cast_speed_+%_for_4_seconds_on_movement_skill_use', 1, 'base_cast_speed_+%', 'flat', '[{"if":"recently","event":"used_movement_skill"}]', 'base_cast_speed_+%'),
('mana_regeneration_+%_for_4_seconds_on_movement_skill_use', 0, 'mana_regeneration_rate_+%', 'flat', '[{"if":"recently","event":"used_movement_skill"}]', 'mana_regeneration_rate_+%'),
('damage_+%_if_used_travel_skill_recently', 0, 'damage_+%', 'flat', '[{"if":"recently","event":"used_travel_skill"}]', 'damage_+%'),
('enchantment_boots_minimum_added_chaos_damage_for_4_seconds_when_crit_4s', 0, 'attack_minimum_added_chaos_damage', 'flat', '[{"if":"recently","event":"taken_crit"}]', 'attack_minimum_added_chaos_damage'),
('enchantment_boots_minimum_added_chaos_damage_for_4_seconds_when_crit_4s', 1, 'spell_minimum_added_chaos_damage', 'flat', '[{"if":"recently","event":"taken_crit"}]', 'spell_minimum_added_chaos_damage'),
('enchantment_boots_maximum_added_chaos_damage_for_4_seconds_when_crit_4s', 0, 'attack_maximum_added_chaos_damage', 'flat', '[{"if":"recently","event":"taken_crit"}]', 'attack_maximum_added_chaos_damage'),
('enchantment_boots_maximum_added_chaos_damage_for_4_seconds_when_crit_4s', 1, 'spell_maximum_added_chaos_damage', 'flat', '[{"if":"recently","event":"taken_crit"}]', 'spell_maximum_added_chaos_damage'),
('extra_damage_taken_from_crit_-%_if_taken_critical_strike_recently', 0, 'base_self_critical_strike_multiplier_-%', 'flat', '[{"if":"recently","event":"taken_crit"}]', 'base_self_critical_strike_multiplier_-%'),
('area_of_effect_+%_if_have_stunned_an_enemy_recently', 0, 'base_skill_area_of_effect_+%', 'flat', '[{"if":"recently","event":"stunned_enemy"}]', 'base_skill_area_of_effect_+%'),
('chance_to_deal_double_damage_%_if_have_stunned_an_enemy_recently', 0, 'chance_to_deal_double_damage_%', 'flat', '[{"if":"recently","event":"stunned_enemy"}]', 'chance_to_deal_double_damage_%')
ON CONFLICT(stat, ord) DO NOTHING;
INSERT INTO fx_stats (stat, kind, handled, ref) VALUES
('gain_X_life_on_stun', 'trigger', 'rule', 'life_on_stun'),
('lose_%_of_life_on_crit', 'trigger', 'rule', 'lose_life_on_crit'),
('lose_%_of_es_on_crit', 'trigger', 'rule', 'lose_es_on_crit'),
('energy_shield_%_to_lose_on_block', 'trigger', 'rule', 'lose_es_on_block'),
('lose_%_of_mana_when_you_use_an_attack_skill', 'trigger', 'rule', 'lose_mana_on_attack_skill'),
('lose_x_mana_when_you_use_skill', 'trigger', 'rule', 'lose_mana_on_skill'),
('maximum_energy_shield_%_lost_on_kill', 'trigger', 'rule', 'lose_es_on_kill'),
('maximum_life_%_lost_on_kill', 'trigger', 'rule', 'lose_life_on_kill')
ON CONFLICT(stat) DO UPDATE SET handled = 'rule', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref) VALUES
('enchantment_boots_mana_regeneration_rate_+%_if_cast_spell_recently', 'conditional number', 'modifier', 'mana_regeneration_rate_+% flat, by its condition; its event is recorded by a rule'),
('attack_and_cast_speed_+%_for_4_seconds_on_movement_skill_use', 'conditional number', 'modifier', 'attack_speed_+% flat, by its condition; its event is recorded by a rule'),
('mana_regeneration_+%_for_4_seconds_on_movement_skill_use', 'conditional number', 'modifier', 'mana_regeneration_rate_+% flat, by its condition; its event is recorded by a rule'),
('damage_+%_if_used_travel_skill_recently', 'conditional number', 'modifier', 'damage_+% flat, by its condition; its event is recorded by a rule'),
('enchantment_boots_minimum_added_chaos_damage_for_4_seconds_when_crit_4s', 'conditional number', 'modifier', 'attack_minimum_added_chaos_damage flat, by its condition; its event is recorded by a rule'),
('enchantment_boots_maximum_added_chaos_damage_for_4_seconds_when_crit_4s', 'conditional number', 'modifier', 'attack_maximum_added_chaos_damage flat, by its condition; its event is recorded by a rule'),
('extra_damage_taken_from_crit_-%_if_taken_critical_strike_recently', 'conditional number', 'modifier', 'base_self_critical_strike_multiplier_-% flat, by its condition; its event is recorded by a rule'),
('area_of_effect_+%_if_have_stunned_an_enemy_recently', 'conditional number', 'modifier', 'base_skill_area_of_effect_+% flat, by its condition; its event is recorded by a rule'),
('chance_to_deal_double_damage_%_if_have_stunned_an_enemy_recently', 'conditional number', 'modifier', 'chance_to_deal_double_damage_% flat, by its condition; its event is recorded by a rule')
ON CONFLICT(stat) DO UPDATE SET kind = 'conditional number', handled = 'modifier', ref = excluded.ref, why = NULL, fit = NULL;
