-- Ailments and Blind on the monster whose hit reached the character, as fx rules on when_hit and block. Those events
-- now carry what their site had (the character's stats as it read them, its time for the hit, the monster's id), so
-- the new "ailment" action inflicts through the same ailment code as before, and the debuff action can blind an
-- attacker. Also written down: the chances a hit against the character has to bleed, maim, sap and scorch it.
INSERT INTO fx_actions (id, takes, built, origin, note) VALUES
('ailment', 'ailment: chill, shock, freeze, scorch, brittle, sap; value: a chill''s own effect when its source states one', 1, 'Chance to Chill Attackers for 4 seconds on Block', 'An ailment without damage on the trigger''s enemy (the monster whose hit reached the actor), for the duration timer; Scorch, Brittle and Sap run their own length. Duration modifiers, immunity and spreading are the ailment code''s.')
ON CONFLICT(id) DO NOTHING;
UPDATE fx_actions SET note = note || ' On when_hit and block the enemy is the attacker (blinded works there).' WHERE id = 'debuff';
DELETE FROM fx_modifiers WHERE stat = 'blind_enemies_when_hit_%_chance';
DELETE FROM fx_quantities WHERE id = 'blind_enemies_when_hit_%_chance';
INSERT INTO fx_rules (id, book, title, replaces, status, origin) VALUES
('freeze_attacker_when_hit', 'ailments', 'Chance to Freeze Enemies for 1 second when they Hit you', 'chance_to_freeze_enemies_for_1_second_when_hit_%', 'built', 'Monsters.cs'),
('chill_attacker_when_hit', 'ailments', 'Chill Enemy for X seconds when Hit, reducing their Action Speed by 30%', 'chill_enemy_when_hit_duration_ms', 'built', 'MonsterAilments.cs'),
('chill_attacker_on_block', 'ailments', 'Chance to Chill Attackers for 4 seconds on Block', 'chill_attackers_for_4_seconds_on_block_%_chance', 'built', 'MonsterAilments.cs'),
('shock_attacker_on_block', 'ailments', 'Chance to Shock Attackers for 4 seconds on Block', 'shock_attackers_for_4_seconds_on_block_%_chance', 'built', 'MonsterAilments.cs'),
('scorch_attacker_on_block', 'ailments', 'Chance to Scorch Enemies when you Block their Damage', 'chance_to_inflict_scorch_on_enemy_on_block_%', 'built', 'MonsterAlternateAilments.cs'),
('brittle_attacker_on_block', 'ailments', 'Chance to inflict Brittle on Enemies when you Block their Damage', 'chance_to_inflict_brittle_on_enemy_on_block_%', 'built', 'MonsterAlternateAilments.cs'),
('sap_attacker_on_block', 'ailments', 'Chance to Sap Enemies when you Block their Damage', 'chance_to_inflict_sap_on_enemy_on_block_%', 'built', 'MonsterAlternateAilments.cs'),
('blind_attacker_when_hit', 'ailments', 'Chance to Blind Enemies when they Hit you', 'blind_enemies_when_hit_%_chance', 'built', 'Monsters.cs');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('freeze_attacker_when_hit', 0, 'stat', 'chance_to_freeze_enemies_for_1_second_when_hit_%'),
('chill_attacker_when_hit', 0, 'stat', 'chill_enemy_when_hit_duration_ms'),
('chill_attacker_on_block', 0, 'stat', 'chill_attackers_for_4_seconds_on_block_%_chance'),
('shock_attacker_on_block', 0, 'stat', 'shock_attackers_for_4_seconds_on_block_%_chance'),
('scorch_attacker_on_block', 0, 'stat', 'chance_to_inflict_scorch_on_enemy_on_block_%'),
('brittle_attacker_on_block', 0, 'stat', 'chance_to_inflict_brittle_on_enemy_on_block_%'),
('sap_attacker_on_block', 0, 'stat', 'chance_to_inflict_sap_on_enemy_on_block_%'),
('blind_attacker_when_hit', 0, 'stat', 'blind_enemies_when_hit_%_chance');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('freeze_attacker_when_hit', 0, 'when_hit', '{}'),
('chill_attacker_when_hit', 0, 'when_hit', '{}'),
('chill_attacker_on_block', 0, 'block', '{}'),
('shock_attacker_on_block', 0, 'block', '{}'),
('scorch_attacker_on_block', 0, 'block', '{}'),
('brittle_attacker_on_block', 0, 'block', '{}'),
('sap_attacker_on_block', 0, 'block', '{}'),
('blind_attacker_when_hit', 0, 'when_hit', '{}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('freeze_attacker_when_hit', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chill_attacker_on_block', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('shock_attacker_on_block', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('scorch_attacker_on_block', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('brittle_attacker_on_block', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('sap_attacker_on_block', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('blind_attacker_when_hit', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('freeze_attacker_when_hit', 0, 'ailment', '{"ailment":"freeze"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":1}}]'),
('chill_attacker_when_hit', 0, 'ailment', '{"ailment":"chill"}', 'enemy', '{}', '{"fixed":30}', '[{"timer":"duration","seconds":{"from":"roll","scale":0.001}}]'),
('chill_attacker_on_block', 0, 'ailment', '{"ailment":"chill"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('shock_attacker_on_block', 0, 'ailment', '{"ailment":"shock"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('scorch_attacker_on_block', 0, 'ailment', '{"ailment":"scorch"}', 'enemy', '{}', '{}', '[]'),
('brittle_attacker_on_block', 0, 'ailment', '{"ailment":"brittle"}', 'enemy', '{}', '{}', '[]'),
('sap_attacker_on_block', 0, 'ailment', '{"ailment":"sap"}', 'enemy', '{}', '{}', '[]'),
('blind_attacker_when_hit', 0, 'debuff', '{"debuff":"blinded"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]');
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES
('chance_to_freeze_enemies_for_1_second_when_hit_%', 'trigger', 'rule', 'freeze_attacker_when_hit', 'Monsters.cs'),
('chill_enemy_when_hit_duration_ms', 'trigger', 'rule', 'chill_attacker_when_hit', 'MonsterAilments.cs'),
('chill_attackers_for_4_seconds_on_block_%_chance', 'trigger', 'rule', 'chill_attacker_on_block', 'MonsterAilments.cs'),
('shock_attackers_for_4_seconds_on_block_%_chance', 'trigger', 'rule', 'shock_attacker_on_block', 'MonsterAilments.cs'),
('chance_to_inflict_scorch_on_enemy_on_block_%', 'trigger', 'rule', 'scorch_attacker_on_block', 'MonsterAlternateAilments.cs'),
('chance_to_inflict_brittle_on_enemy_on_block_%', 'trigger', 'rule', 'brittle_attacker_on_block', 'MonsterAlternateAilments.cs'),
('chance_to_inflict_sap_on_enemy_on_block_%', 'trigger', 'rule', 'sap_attacker_on_block', 'MonsterAlternateAilments.cs'),
('blind_enemies_when_hit_%_chance', 'trigger', 'rule', 'blind_attacker_when_hit', 'Monsters.cs')
ON CONFLICT(stat) DO UPDATE SET kind = 'trigger', handled = 'rule', ref = excluded.ref, why = NULL, fit = NULL;
INSERT INTO fx_quantities (id, unit, formula, built, origin, note) VALUES
('bleed_chance_against_self', '%', 'code', 1, '#% chance to be inflicted with Bleeding when Hit by an Attack', 'Added to the chance an attack hit against the actor makes it bleed.'),
('maim_chance_against_self', '%', 'code', 1, 'Attack Hits have #% chance to Maim you for 4 seconds', 'The chance an attack hit against the actor maims it.'),
('sap_chance_against_self', '%', 'code', 1, '#% chance to be Sapped when Hit', 'Added to the chance a hit against the actor saps it.'),
('scorch_chance_against_self', '%', 'code', 1, '#% chance to be Scorched when Hit', 'Added to the chance a hit against the actor scorches it.')
ON CONFLICT(id) DO NOTHING;
INSERT INTO fx_modifiers (stat, ord, quantity, how) VALUES
('receive_bleeding_chance_%_when_hit_by_attack', 0, 'bleed_chance_against_self', 'flat'),
('chance_to_be_maimed_when_hit_%', 0, 'maim_chance_against_self', 'flat'),
('chance_to_be_sapped_when_hit_%', 0, 'sap_chance_against_self', 'flat'),
('chance_to_be_scorched_when_hit_%', 0, 'scorch_chance_against_self', 'flat')
ON CONFLICT(stat, ord) DO NOTHING;
INSERT INTO fx_stats (stat, kind, handled, ref) VALUES
('receive_bleeding_chance_%_when_hit_by_attack', 'trigger', 'modifier', 'bleed_chance_against_self flat'),
('chance_to_be_maimed_when_hit_%', 'trigger', 'modifier', 'maim_chance_against_self flat'),
('chance_to_be_sapped_when_hit_%', 'trigger', 'modifier', 'sap_chance_against_self flat'),
('chance_to_be_scorched_when_hit_%', 'trigger', 'modifier', 'scorch_chance_against_self flat')
ON CONFLICT(stat) DO UPDATE SET handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, why, fit) VALUES
('flask_charges_recovered_per_3_seconds', 'trigger', 'code', 'Flask charges are gained under the flask bar''s own gate, with each flask''s gain multiplier and a kept fraction, and sent as an inventory change.', 'Timed rules (interval 3 seconds) with a flask charge action; the flask session keeps the fractions, as the charge system keeps the charge timers.'),
('life_flask_charges_recovered_per_3_seconds', 'trigger', 'code', 'Flask charges are gained under the flask bar''s own gate, with each flask''s gain multiplier and a kept fraction, and sent as an inventory change.', 'Timed rules (interval 3 seconds) with a flask charge action; the flask session keeps the fractions, as the charge system keeps the charge timers.'),
('life_flask_charges_recovered_per_3_seconds_on_low_life', 'trigger', 'code', 'Flask charges are gained under the flask bar''s own gate, with each flask''s gain multiplier and a kept fraction, and sent as an inventory change.', 'Timed rules (interval 3 seconds) with a flask charge action; the flask session keeps the fractions, as the charge system keeps the charge timers.'),
('mana_flasks_gain_X_charges_every_3_seconds', 'trigger', 'code', 'Flask charges are gained under the flask bar''s own gate, with each flask''s gain multiplier and a kept fraction, and sent as an inventory change.', 'Timed rules (interval 3 seconds) with a flask charge action; the flask session keeps the fractions, as the charge system keeps the charge timers.'),
('utility_flask_charges_recovered_per_3_seconds', 'trigger', 'code', 'Flask charges are gained under the flask bar''s own gate, with each flask''s gain multiplier and a kept fraction, and sent as an inventory change.', 'Timed rules (interval 3 seconds) with a flask charge action; the flask session keeps the fractions, as the charge system keeps the charge timers.'),
('gain_shrine_buff_on_rare_or_unique_kill_centiseconds', 'trigger', 'code', 'The shrine module queues the buff for its own tick, which picks the shrine and presents it.', 'A rule on kill (rare or unique) and timed rules with a shrine buff action that queues it for the shrine module.'),
('gain_shrine_buff_every_x_ms', 'trigger', 'code', 'The shrine module queues the buff for its own tick, which picks the shrine and presents it.', 'A rule on kill (rare or unique) and timed rules with a shrine buff action that queues it for the shrine module.'),
('gain_shrine_buff_every_x_ms_from_ascendancy', 'trigger', 'code', 'The shrine module queues the buff for its own tick, which picks the shrine and presents it.', 'A rule on kill (rare or unique) and timed rules with a shrine buff action that queues it for the shrine module.'),
('gain_blood_shrine_buff_every_x_ms_from_unique', 'trigger', 'code', 'The shrine module queues the buff for its own tick, which picks the shrine and presents it.', 'A rule on kill (rare or unique) and timed rules with a shrine buff action that queues it for the shrine module.'),
('chance_to_gain_vaal_soul_on_kill_%', 'trigger', 'code', 'Rolled inside the Vaal soul gain, after soul gain prevention and the kill''s own soul.', 'A rule on kill with a Vaal soul action, conditional on the kill having given a soul.'),
('gain_siphoning_charge_on_skill_use_%_chance', 'trigger', 'code', 'Siphoning Charges keep their own maximum, duration and degeneration in their session.', 'A rule on skill_use with a siphoning charge action (the charge action with a new type).'),
('enemy_shock_on_kill', 'trigger', 'code', 'Worked out where a hit kills, from the hit''s own stats and the dead monster''s ailments.', 'Rules on kill with the ailment action on the corpse and on enemies_near, once a kill carries the killing hit''s stats.'),
('shock_X_nearby_enemies_for_2_s_on_killing_shocked_enemy', 'trigger', 'code', 'Worked out where a hit kills, from the hit''s own stats and the dead monster''s ailments.', 'Rules on kill with the ailment action on the corpse and on enemies_near, once a kill carries the killing hit''s stats.'),
('gain_x_rage_when_hit', 'trigger', 'code', 'Rage keeps its gains as read when the stats are built, with its own cooldown between gains.', 'A rule on when_hit with a rage action and the cooldown timer.')
ON CONFLICT(stat) DO UPDATE SET why = excluded.why, fit = excluded.fit;
