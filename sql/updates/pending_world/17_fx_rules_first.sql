-- The first effect rules (effect_rules) written again in the fx tables, part by part: the same triggers, conditions,
-- values and actions, so each behaves as its effect_rules row did. A stat with a rule here is taken from here and its
-- effect_rules row is left out (EffectRules.Current). replaces: the stat the older code read, for the rules that have such code.
INSERT INTO fx_rules (id, book, title, replaces, status) VALUES
('attack_trigger_on_hitting_bleeding_enemy_%', 'items', 'Trigger this Attack when you Hit a Bleeding Enemy', NULL, 'built'),
('attack_trigger_on_kill_%', 'items', 'Trigger this Attack on Kill, where the enemy died', NULL, 'built'),
('attack_trigger_on_melee_critical_hit_%', 'items', 'Trigger this Attack on Melee Critical Strike', NULL, 'built'),
('attack_trigger_on_melee_hit_%', 'items', 'Trigger this Attack on Melee Hit', NULL, 'built'),
('attack_trigger_on_melee_hit_with_atleast_150_strength_%', 'items', 'Trigger this Attack on Melee Hit if you have at least 150 Strength', NULL, 'built'),
('cast_on_attack_use_%', 'items', 'Trigger this Spell when you use an Attack, where the attack was aimed', NULL, 'built'),
('cast_on_crit_%', 'items', 'Trigger this Spell when you deal a Critical Strike', NULL, 'built'),
('cast_on_gain_skill', 'items', 'A skill that is on while its item is worn: as its effect_skills row has it (aura, pulse)', NULL, 'built'),
('cast_on_gain_skill:BlindingAura', 'items', 'Blinding Aura: nearby enemies are blinded while it is worn', NULL, 'built'),
('cast_on_hit_%', 'items', 'Trigger this Spell when you Hit', NULL, 'built'),
('cast_on_skill_use_%', 'items', 'Trigger this Spell when you use a Skill', NULL, 'built'),
('cast_when_cast_curse_%', 'items', 'Trigger this Spell when you Cast a Curse Spell', NULL, 'built'),
('cast_when_hit_%', 'items', 'Trigger this Spell when Hit, around the character', NULL, 'built'),
('chance_to_cast_on_kill_%', 'items', 'Trigger this Spell on Kill, where the enemy died (Stalking Pustule)', NULL, 'built'),
('chance_to_cast_on_kill_%_target_self', 'items', 'Trigger this Spell on Kill (it targets its user: a nova around the character)', NULL, 'built'),
('chance_to_gain_onslaught_on_kill_%', 'items', '% chance to gain Onslaught for 4 seconds on Kill; onslaught_time_granted_on_kill_ms replaces the 4 seconds', 'chance_to_gain_onslaught_on_kill_%', 'built'),
('chance_to_gain_onslaught_on_kill_for_10_seconds_%', 'items', '% chance to gain Onslaught for 10 seconds on Kill', 'chance_to_gain_onslaught_on_kill_for_10_seconds_%', 'built'),
('chance_to_gain_onslaught_on_kill_for_4_seconds_%', 'items', '% chance to gain Onslaught for 4 seconds on Kill', 'chance_to_gain_onslaught_on_kill_for_4_seconds_%', 'built'),
('gain_adrenaline_for_X_seconds_on_kill', 'items', 'You gain Adrenaline for X seconds on Kill (Death Rush)', 'gain_adrenaline_for_X_seconds_on_kill', 'built'),
('gain_onslaught_on_hit_duration_ms', 'items', 'You gain Onslaught for X seconds on Hit', 'gain_onslaught_on_hit_duration_ms', 'built'),
('onslaught_buff_duration_on_kill_ms', 'items', 'You gain Onslaught for X seconds on Kill', 'onslaught_buff_duration_on_kill_ms', 'built'),
('onslaught_on_crit_duration_ms', 'items', 'You gain Onslaught for X seconds on Critical Strike (Kongor''s Undying Rage)', 'onslaught_on_crit_duration_ms', 'built'),
('skill_has_trigger_from_unique_item:FieryImpactHeistMaceImplicit', 'items', 'Trigger Level X Fiery Impact on Melee Hit with this Weapon (any melee hit for now: a hit does not say which weapon made it)', NULL, 'built'),
('trigger_on_block_%_chance', 'items', 'Trigger this skill when you Block', NULL, 'built'),
('trigger_on_bow_attack_%', 'items', 'Trigger this skill when you Attack with a Bow', NULL, 'built'),
('trigger_on_hit_against_rare_or_unique_if_no_marked_enemy', 'items', 'Trigger Level X <Mark> when you Hit a Rare or Unique Enemy and have no Mark', NULL, 'built'),
('trigger_on_hit_vs_frozen_enemy_%', 'items', 'Trigger this skill when you Hit a Frozen Enemy', NULL, 'built'),
('trigger_on_ignited_enemy_death', 'items', 'Trigger this skill when an Ignited Enemy is Killed (Ignition Blast), where the enemy died', NULL, 'built'),
('trigger_on_skill_use_from_chest_%', 'items', 'Trigger this skill when you use a Socketed Skill. Fires for any skill use: there is no fact yet for a skill socketed in the item', NULL, 'built'),
('unique_mjolner_lightning_spells_triggered', 'items', 'The mutated Mjolner''s Lightning Bolt on Hit. A guess from the mod''s name: every attack hit, within the skill''s cooldown', NULL, 'built');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('attack_trigger_on_hitting_bleeding_enemy_%', 0, 'skill', 'attack_trigger_on_hitting_bleeding_enemy_%'),
('attack_trigger_on_kill_%', 0, 'skill', 'attack_trigger_on_kill_%'),
('attack_trigger_on_melee_critical_hit_%', 0, 'skill', 'attack_trigger_on_melee_critical_hit_%'),
('attack_trigger_on_melee_hit_%', 0, 'skill', 'attack_trigger_on_melee_hit_%'),
('attack_trigger_on_melee_hit_with_atleast_150_strength_%', 0, 'skill', 'attack_trigger_on_melee_hit_with_atleast_150_strength_%'),
('cast_on_attack_use_%', 0, 'skill', 'cast_on_attack_use_%'),
('cast_on_crit_%', 0, 'skill', 'cast_on_crit_%'),
('cast_on_gain_skill', 0, 'skill', 'cast_on_gain_skill'),
('cast_on_gain_skill:BlindingAura', 0, 'skill', 'cast_on_gain_skill'),
('cast_on_hit_%', 0, 'skill', 'cast_on_hit_%'),
('cast_on_skill_use_%', 0, 'skill', 'cast_on_skill_use_%'),
('cast_when_cast_curse_%', 0, 'skill', 'cast_when_cast_curse_%'),
('cast_when_hit_%', 0, 'skill', 'cast_when_hit_%'),
('chance_to_cast_on_kill_%', 0, 'skill', 'chance_to_cast_on_kill_%'),
('chance_to_cast_on_kill_%_target_self', 0, 'skill', 'chance_to_cast_on_kill_%_target_self'),
('chance_to_gain_onslaught_on_kill_%', 0, 'stat', 'chance_to_gain_onslaught_on_kill_%'),
('chance_to_gain_onslaught_on_kill_for_10_seconds_%', 0, 'stat', 'chance_to_gain_onslaught_on_kill_for_10_seconds_%'),
('chance_to_gain_onslaught_on_kill_for_4_seconds_%', 0, 'stat', 'chance_to_gain_onslaught_on_kill_for_4_seconds_%'),
('gain_adrenaline_for_X_seconds_on_kill', 0, 'stat', 'gain_adrenaline_for_X_seconds_on_kill'),
('gain_onslaught_on_hit_duration_ms', 0, 'stat', 'gain_onslaught_on_hit_duration_ms'),
('onslaught_buff_duration_on_kill_ms', 0, 'stat', 'onslaught_buff_duration_on_kill_ms'),
('onslaught_on_crit_duration_ms', 0, 'stat', 'onslaught_on_crit_duration_ms'),
('skill_has_trigger_from_unique_item:FieryImpactHeistMaceImplicit', 0, 'skill', 'skill_has_trigger_from_unique_item'),
('trigger_on_block_%_chance', 0, 'skill', 'trigger_on_block_%_chance'),
('trigger_on_bow_attack_%', 0, 'skill', 'trigger_on_bow_attack_%'),
('trigger_on_hit_against_rare_or_unique_if_no_marked_enemy', 0, 'skill', 'trigger_on_hit_against_rare_or_unique_if_no_marked_enemy'),
('trigger_on_hit_vs_frozen_enemy_%', 0, 'skill', 'trigger_on_hit_vs_frozen_enemy_%'),
('trigger_on_ignited_enemy_death', 0, 'skill', 'trigger_on_ignited_enemy_death'),
('trigger_on_skill_use_from_chest_%', 0, 'skill', 'trigger_on_skill_use_from_chest_%'),
('unique_mjolner_lightning_spells_triggered', 0, 'skill', 'unique_mjolner_lightning_spells_triggered');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('attack_trigger_on_hitting_bleeding_enemy_%', 0, 'hit', '{}'),
('attack_trigger_on_kill_%', 0, 'kill', '{}'),
('attack_trigger_on_melee_critical_hit_%', 0, 'hit', '{}'),
('attack_trigger_on_melee_hit_%', 0, 'hit', '{}'),
('attack_trigger_on_melee_hit_with_atleast_150_strength_%', 0, 'hit', '{}'),
('cast_on_attack_use_%', 0, 'skill_use', '{}'),
('cast_on_crit_%', 0, 'hit', '{}'),
('cast_on_gain_skill', 0, 'active', '{}'),
('cast_on_gain_skill:BlindingAura', 0, 'active', '{}'),
('cast_on_hit_%', 0, 'hit', '{}'),
('cast_on_skill_use_%', 0, 'skill_use', '{}'),
('cast_when_cast_curse_%', 0, 'skill_use', '{}'),
('cast_when_hit_%', 0, 'when_hit', '{}'),
('chance_to_cast_on_kill_%', 0, 'kill', '{}'),
('chance_to_cast_on_kill_%_target_self', 0, 'kill', '{}'),
('chance_to_gain_onslaught_on_kill_%', 0, 'kill', '{}'),
('chance_to_gain_onslaught_on_kill_for_10_seconds_%', 0, 'kill', '{}'),
('chance_to_gain_onslaught_on_kill_for_4_seconds_%', 0, 'kill', '{}'),
('gain_adrenaline_for_X_seconds_on_kill', 0, 'kill', '{}'),
('gain_onslaught_on_hit_duration_ms', 0, 'hit', '{}'),
('onslaught_buff_duration_on_kill_ms', 0, 'kill', '{}'),
('onslaught_on_crit_duration_ms', 0, 'hit', '{}'),
('skill_has_trigger_from_unique_item:FieryImpactHeistMaceImplicit', 0, 'hit', '{}'),
('trigger_on_block_%_chance', 0, 'block', '{}'),
('trigger_on_bow_attack_%', 0, 'skill_use', '{}'),
('trigger_on_hit_against_rare_or_unique_if_no_marked_enemy', 0, 'hit', '{}'),
('trigger_on_hit_vs_frozen_enemy_%', 0, 'hit', '{}'),
('trigger_on_ignited_enemy_death', 0, 'kill', '{}'),
('trigger_on_skill_use_from_chest_%', 0, 'skill_use', '{}'),
('unique_mjolner_lightning_spells_triggered', 0, 'hit', '{}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('attack_trigger_on_hitting_bleeding_enemy_%', 0, 0, 'skill_type', 0, '{"is":"attack"}'),
('attack_trigger_on_hitting_bleeding_enemy_%', 0, 1, 'enemy_state', 0, '{"is":"bleeding"}'),
('attack_trigger_on_hitting_bleeding_enemy_%', 0, 2, 'chance', 0, '{"percent":{"from":"roll"}}'),
('attack_trigger_on_hitting_bleeding_enemy_%', 0, 3, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('attack_trigger_on_kill_%', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('attack_trigger_on_kill_%', 0, 1, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('attack_trigger_on_melee_critical_hit_%', 0, 0, 'skill_type', 0, '{"is":"melee"}'),
('attack_trigger_on_melee_critical_hit_%', 0, 1, 'critical', 0, '{}'),
('attack_trigger_on_melee_critical_hit_%', 0, 2, 'chance', 0, '{"percent":{"from":"roll"}}'),
('attack_trigger_on_melee_critical_hit_%', 0, 3, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('attack_trigger_on_melee_hit_%', 0, 0, 'skill_type', 0, '{"is":"melee"}'),
('attack_trigger_on_melee_hit_%', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}'),
('attack_trigger_on_melee_hit_%', 0, 2, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('attack_trigger_on_melee_hit_with_atleast_150_strength_%', 0, 0, 'stat_at_least', 0, '{"stat":"strength","min":150}'),
('attack_trigger_on_melee_hit_with_atleast_150_strength_%', 0, 1, 'skill_type', 0, '{"is":"melee"}'),
('attack_trigger_on_melee_hit_with_atleast_150_strength_%', 0, 2, 'chance', 0, '{"percent":{"from":"roll"}}'),
('attack_trigger_on_melee_hit_with_atleast_150_strength_%', 0, 3, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('cast_on_attack_use_%', 0, 0, 'skill_type', 0, '{"is":"attack"}'),
('cast_on_attack_use_%', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}'),
('cast_on_attack_use_%', 0, 2, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('cast_on_crit_%', 0, 0, 'critical', 0, '{}'),
('cast_on_crit_%', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}'),
('cast_on_crit_%', 0, 2, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('cast_on_gain_skill:BlindingAura', 0, 0, 'source_is', 0, '{"name":"BlindingAura"}'),
('cast_on_hit_%', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('cast_on_hit_%', 0, 1, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('cast_on_skill_use_%', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('cast_on_skill_use_%', 0, 1, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('cast_when_cast_curse_%', 0, 0, 'skill_type', 0, '{"is":"curse"}'),
('cast_when_cast_curse_%', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}'),
('cast_when_cast_curse_%', 0, 2, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('cast_when_hit_%', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('cast_when_hit_%', 0, 1, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('chance_to_cast_on_kill_%', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chance_to_cast_on_kill_%', 0, 1, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('chance_to_cast_on_kill_%_target_self', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chance_to_cast_on_kill_%_target_self', 0, 1, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('chance_to_gain_onslaught_on_kill_%', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chance_to_gain_onslaught_on_kill_for_10_seconds_%', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chance_to_gain_onslaught_on_kill_for_4_seconds_%', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('onslaught_on_crit_duration_ms', 0, 0, 'critical', 0, '{}'),
('skill_has_trigger_from_unique_item:FieryImpactHeistMaceImplicit', 0, 0, 'source_is', 0, '{"name":"FieryImpactHeistMaceImplicit"}'),
('skill_has_trigger_from_unique_item:FieryImpactHeistMaceImplicit', 0, 1, 'skill_type', 0, '{"is":"melee"}'),
('skill_has_trigger_from_unique_item:FieryImpactHeistMaceImplicit', 0, 2, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('trigger_on_block_%_chance', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('trigger_on_block_%_chance', 0, 1, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('trigger_on_bow_attack_%', 0, 0, 'skill_type', 0, '{"is":"bow"}'),
('trigger_on_bow_attack_%', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}'),
('trigger_on_bow_attack_%', 0, 2, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('trigger_on_hit_against_rare_or_unique_if_no_marked_enemy', 0, 0, 'enemy_rarity', 0, '{"is":"rare_or_unique"}'),
('trigger_on_hit_against_rare_or_unique_if_no_marked_enemy', 0, 1, 'has_mark', 1, '{}'),
('trigger_on_hit_against_rare_or_unique_if_no_marked_enemy', 0, 2, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"}}'),
('trigger_on_hit_vs_frozen_enemy_%', 0, 0, 'enemy_state', 0, '{"is":"frozen"}'),
('trigger_on_hit_vs_frozen_enemy_%', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}'),
('trigger_on_hit_vs_frozen_enemy_%', 0, 2, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('trigger_on_ignited_enemy_death', 0, 0, 'enemy_state', 0, '{"is":"ignited"}'),
('trigger_on_ignited_enemy_death', 0, 1, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('trigger_on_skill_use_from_chest_%', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('trigger_on_skill_use_from_chest_%', 0, 1, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}'),
('unique_mjolner_lightning_spells_triggered', 0, 0, 'skill_type', 0, '{"is":"attack"}'),
('unique_mjolner_lightning_spells_triggered', 0, 1, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"},"uses":{"from":"source","name":"uses"}}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('attack_trigger_on_hitting_bleeding_enemy_%', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('attack_trigger_on_kill_%', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('attack_trigger_on_melee_critical_hit_%', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('attack_trigger_on_melee_hit_%', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('attack_trigger_on_melee_hit_with_atleast_150_strength_%', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('cast_on_attack_use_%', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('cast_on_crit_%', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('cast_on_gain_skill', 0, 'skill', '{"skill":"granted"}', 'self', '{}', '{}', '[]'),
('cast_on_gain_skill:BlindingAura', 0, 'quantity', '{"stats":{"local_display_nearby_enemies_are_blinded":1,"virtual_nearby_enemies_are_blinded":1}}', 'self', '{}', '{}', '[{"timer":"source"}]'),
('cast_on_hit_%', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('cast_on_skill_use_%', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('cast_when_cast_curse_%', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('cast_when_hit_%', 0, 'skill', '{"skill":"granted"}', 'self', '{}', '{}', '[]'),
('chance_to_cast_on_kill_%', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('chance_to_cast_on_kill_%_target_self', 0, 'skill', '{"skill":"granted"}', 'self', '{}', '{}', '[]'),
('chance_to_gain_onslaught_on_kill_%', 0, 'buff', '{"buff":"onslaught"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"from":"stat","stat":"onslaught_time_granted_on_kill_ms","scale":0.001,"fallback":4}}]'),
('chance_to_gain_onslaught_on_kill_for_10_seconds_%', 0, 'buff', '{"buff":"onslaught"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":10}}]'),
('chance_to_gain_onslaught_on_kill_for_4_seconds_%', 0, 'buff', '{"buff":"onslaught"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('gain_adrenaline_for_X_seconds_on_kill', 0, 'buff', '{"template":"SentinelAdrenaline"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"from":"roll"}}]'),
('gain_onslaught_on_hit_duration_ms', 0, 'buff', '{"buff":"onslaught"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"from":"roll","scale":0.001}}]'),
('onslaught_buff_duration_on_kill_ms', 0, 'buff', '{"buff":"onslaught"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"from":"roll","scale":0.001}}]'),
('onslaught_on_crit_duration_ms', 0, 'buff', '{"buff":"onslaught"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"from":"roll","scale":0.001}}]'),
('skill_has_trigger_from_unique_item:FieryImpactHeistMaceImplicit', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('trigger_on_block_%_chance', 0, 'skill', '{"skill":"granted"}', 'self', '{}', '{}', '[]'),
('trigger_on_bow_attack_%', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('trigger_on_hit_against_rare_or_unique_if_no_marked_enemy', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('trigger_on_hit_vs_frozen_enemy_%', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('trigger_on_ignited_enemy_death', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('trigger_on_skill_use_from_chest_%', 0, 'skill', '{"skill":"granted"}', 'self', '{}', '{}', '[]'),
('unique_mjolner_lightning_spells_triggered', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]');
-- The conditions these rules use that the first parts list did not have.
INSERT INTO fx_conditions (id, takes, built, origin, note) VALUES
('critical', '', 1, 'Onslaught on critical strike', 'The trigger''s hit was a critical strike.'),
('enemy_state', 'is: frozen, ignited, bleeding, poisoned, marked, killed', 1, 'trigger on hit against a Frozen enemy', 'The trigger''s enemy is in that state (killed: the hit killed it).'),
('source_is', 'name: the skill''s GrantedEffects Id', 1, 'Fiery Impact (a Heist mace implicit)', 'The rule is for the one granted skill of that name, when several skills share the source stat and each has its own trigger.')
ON CONFLICT(id) DO NOTHING;
UPDATE fx_conditions SET takes = 'is: normal, magic, rare, unique, rare_or_unique' WHERE id = 'enemy_rarity';
UPDATE fx_conditions SET takes = 'is: attack, spell, melee, bow, vaal, curse, trap, travel, movement, warcry, strike, slam, channelled, brand, aura, guard' WHERE id = 'skill_type';
UPDATE fx_conditions SET takes = 'seconds: a value; uses: a value (how many uses the cooldown holds)' WHERE id = 'cooldown';
UPDATE fx_conditions SET built = 1, takes = 'stat, min, max' WHERE id = 'stat_at_least';
UPDATE fx_conditions SET built = 0, note = 'Passes while the rule has uses left. Today a use limit is part of "cooldown" (its "uses").' WHERE id = 'uses';
UPDATE fx_values SET takes = 'name: cooldown, uses, level', id = 'source', note = 'A number of the rule''s own source by name: a granted skill''s cooldown, its stored uses or its level.' WHERE id = 'skill_stat';
UPDATE fx_timers SET built = 1 WHERE id = 'source';
-- Each stat's row names its rule.
UPDATE fx_stats SET ref = 'attack_trigger_on_hitting_bleeding_enemy_%' WHERE stat = 'attack_trigger_on_hitting_bleeding_enemy_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'attack_trigger_on_kill_%' WHERE stat = 'attack_trigger_on_kill_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'attack_trigger_on_melee_critical_hit_%' WHERE stat = 'attack_trigger_on_melee_critical_hit_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'attack_trigger_on_melee_hit_%' WHERE stat = 'attack_trigger_on_melee_hit_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'attack_trigger_on_melee_hit_with_atleast_150_strength_%' WHERE stat = 'attack_trigger_on_melee_hit_with_atleast_150_strength_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'cast_on_attack_use_%' WHERE stat = 'cast_on_attack_use_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'cast_on_crit_%' WHERE stat = 'cast_on_crit_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'cast_on_gain_skill' WHERE stat = 'cast_on_gain_skill' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'cast_on_gain_skill:BlindingAura' WHERE stat = 'cast_on_gain_skill' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'cast_on_hit_%' WHERE stat = 'cast_on_hit_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'cast_on_skill_use_%' WHERE stat = 'cast_on_skill_use_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'cast_when_cast_curse_%' WHERE stat = 'cast_when_cast_curse_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'cast_when_hit_%' WHERE stat = 'cast_when_hit_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'chance_to_cast_on_kill_%' WHERE stat = 'chance_to_cast_on_kill_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'chance_to_cast_on_kill_%_target_self' WHERE stat = 'chance_to_cast_on_kill_%_target_self' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'chance_to_gain_onslaught_on_kill_%' WHERE stat = 'chance_to_gain_onslaught_on_kill_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'chance_to_gain_onslaught_on_kill_for_10_seconds_%' WHERE stat = 'chance_to_gain_onslaught_on_kill_for_10_seconds_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'chance_to_gain_onslaught_on_kill_for_4_seconds_%' WHERE stat = 'chance_to_gain_onslaught_on_kill_for_4_seconds_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'gain_adrenaline_for_X_seconds_on_kill' WHERE stat = 'gain_adrenaline_for_X_seconds_on_kill' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'gain_onslaught_on_hit_duration_ms' WHERE stat = 'gain_onslaught_on_hit_duration_ms' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'onslaught_buff_duration_on_kill_ms' WHERE stat = 'onslaught_buff_duration_on_kill_ms' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'onslaught_on_crit_duration_ms' WHERE stat = 'onslaught_on_crit_duration_ms' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'skill_has_trigger_from_unique_item:FieryImpactHeistMaceImplicit' WHERE stat = 'skill_has_trigger_from_unique_item' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'trigger_on_block_%_chance' WHERE stat = 'trigger_on_block_%_chance' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'trigger_on_bow_attack_%' WHERE stat = 'trigger_on_bow_attack_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'trigger_on_hit_against_rare_or_unique_if_no_marked_enemy' WHERE stat = 'trigger_on_hit_against_rare_or_unique_if_no_marked_enemy' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'trigger_on_hit_vs_frozen_enemy_%' WHERE stat = 'trigger_on_hit_vs_frozen_enemy_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'trigger_on_ignited_enemy_death' WHERE stat = 'trigger_on_ignited_enemy_death' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'trigger_on_skill_use_from_chest_%' WHERE stat = 'trigger_on_skill_use_from_chest_%' AND (ref = 'effect_rules' OR ref IS NULL);
UPDATE fx_stats SET ref = 'unique_mjolner_lightning_spells_triggered' WHERE stat = 'unique_mjolner_lightning_spells_triggered' AND (ref = 'effect_rules' OR ref IS NULL);
