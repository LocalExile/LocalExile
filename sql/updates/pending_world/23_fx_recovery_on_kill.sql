-- Recovery on kill (KillRecovery.cs): its 14 stats are numbers of six quantities, some only for a kill of an enemy in
-- a state or per character level, and one rule spends them at a kill. The code does that today: per pool,
-- floor(flat + maximum x percent / 100), once a kill; "cannot recover" stops a pool's share. The rule is written
-- down as 'not built' (the engine has no "recover" action and no source "always" yet), so the loader leaves it out.
INSERT INTO fx_quantities (id, unit, formula, built, origin, note) VALUES
('life_gained_on_kill', '', 'code', 1, 'KillRecovery.cs', 'Life the actor gains when it kills an enemy.'),
('life_%_recovered_on_kill', '', 'code', 1, 'KillRecovery.cs', 'Percent of its maximum life the actor recovers when it kills an enemy.'),
('mana_gained_on_kill', '', 'code', 1, 'KillRecovery.cs', 'Mana the actor gains when it kills an enemy.'),
('mana_%_recovered_on_kill', '', 'code', 1, 'KillRecovery.cs', 'Percent of its maximum mana the actor recovers when it kills an enemy.'),
('energy_shield_gained_on_kill', '', 'code', 1, 'KillRecovery.cs', 'Energy shield the actor gains when it kills an enemy.'),
('energy_shield_%_recovered_on_kill', '', 'code', 1, 'KillRecovery.cs', 'Percent of its maximum energy shield the actor recovers when it kills an enemy.')
ON CONFLICT(id) DO NOTHING;
INSERT INTO fx_modifiers (stat, ord, quantity, how, scale, conditions, target, note) VALUES
('base_life_gained_on_enemy_death', 0, 'life_gained_on_kill', 'flat', 1, '[]', 'self', 'Gain {0} Life per Enemy Killed'),
('life_gained_on_enemy_death_per_level', 0, 'life_gained_on_kill', 'flat', 1, '[{"if":"count","of":"level"}]', 'self', 'Gain {0} Life on Kill per Level'),
('recover_%_maximum_life_on_kill', 0, 'life_%_recovered_on_kill', 'flat', 1, '[]', 'self', 'Recover {0}% of Life on Kill'),
('base_mana_gained_on_enemy_death', 0, 'mana_gained_on_kill', 'flat', 1, '[]', 'self', 'Gain {0} Mana per Enemy Killed'),
('mana_gained_on_enemy_death_per_level', 0, 'mana_gained_on_kill', 'flat', 1, '[{"if":"count","of":"level"}]', 'self', 'Gain {0} Mana on Kill per Level'),
('recover_%_maximum_mana_on_kill', 0, 'mana_%_recovered_on_kill', 'flat', 1, '[]', 'self', 'Recover {0}% of Mana on Kill'),
('maximum_mana_%_gained_on_kill', 0, 'mana_%_recovered_on_kill', 'flat', 1, '[]', 'self', 'Recover {0}% of Mana on Kill'),
('base_energy_shield_gained_on_enemy_death', 0, 'energy_shield_gained_on_kill', 'flat', 1, '[]', 'self', 'Gain {0} Energy Shield per Enemy Killed'),
('energy_shield_gained_on_enemy_death_per_level', 0, 'energy_shield_gained_on_kill', 'flat', 1, '[{"if":"count","of":"level"}]', 'self', 'Gain {0} Energy Shield on Kill per Level'),
('recover_energy_shield_%_on_kill', 0, 'energy_shield_%_recovered_on_kill', 'flat', 1, '[]', 'self', 'Recover {0}% of Energy Shield on Kill'),
('life_gained_on_killing_ignited_enemies', 0, 'life_gained_on_kill', 'flat', 1, '[{"if":"enemy_state","is":"ignited"}]', 'self', 'Gain {0} Life per Ignited Enemy Killed'),
('recover_%_maximum_life_on_killing_chilled_enemy', 0, 'life_%_recovered_on_kill', 'flat', 1, '[{"if":"enemy_state","is":"chilled"}]', 'self', 'Recover {0}% of Life on Killing a Chilled Enemy'),
('gain_X_energy_shield_on_killing_shocked_enemy', 0, 'energy_shield_gained_on_kill', 'flat', 1, '[{"if":"enemy_state","is":"shocked"}]', 'self', '{0:+d} Energy Shield gained on Killing a Shocked Enemy'),
('recover_X_mana_on_killing_frozen_enemy', 0, 'mana_gained_on_kill', 'flat', 1, '[{"if":"enemy_state","is":"frozen"}]', 'self', '{0:+d} Mana gained on Killing a Frozen Enemy');
INSERT INTO fx_conditions (id, takes, built, origin, note) VALUES
('enemy_state_at_death', 'is: ignited, chilled, shocked, frozen', 0, 'Gain Life per Ignited Enemy Killed', 'Recorded as enemy_state on a kill: the killed enemy was in that state when it died. The code answers it for recovery on kill; a rule''s "kill" trigger answers frozen, ignited, bleeding and poisoned.')
ON CONFLICT(id) DO NOTHING;
INSERT INTO fx_rules (id, book, title, replaces, status, origin, note) VALUES
('recover_on_kill', 'recovery', 'On kill, recover the life, mana and energy shield the kill-recovery quantities add up to', NULL, 'not built', 'KillRecovery.cs', 'The code does this today. Per pool: floor(flat + maximum x percent / 100), once a kill, with no recovery rate applied; "cannot recover" life or energy shield stops that pool''s share.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES ('recover_on_kill', 0, 'always', '');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES ('recover_on_kill', 0, 'kill', '{}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('recover_on_kill', 0, 'recover', '{"pool":"life"}', 'self', '{}', '{"from":"quantity","flat":"life_gained_on_kill","percent_of_maximum":"life_%_recovered_on_kill"}', '[]'),
('recover_on_kill', 1, 'recover', '{"pool":"mana"}', 'self', '{}', '{"from":"quantity","flat":"mana_gained_on_kill","percent_of_maximum":"mana_%_recovered_on_kill"}', '[]'),
('recover_on_kill', 2, 'recover', '{"pool":"energy shield"}', 'self', '{}', '{"from":"quantity","flat":"energy_shield_gained_on_kill","percent_of_maximum":"energy_shield_%_recovered_on_kill"}', '[]');
INSERT INTO fx_values (id, takes, built, origin, note) VALUES
('quantity', 'flat: an fx_quantities id; percent_of_maximum: another, taken as a percent of the pool''s maximum', 0, 'recovery on kill', 'What one of the actor''s quantities adds up to now, from every modifier of it.')
ON CONFLICT(id) DO NOTHING;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('base_life_gained_on_enemy_death', 'number', 'modifier', 'life_gained_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('life_gained_on_enemy_death_per_level', 'number', 'modifier', 'life_gained_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('recover_%_maximum_life_on_kill', 'number', 'modifier', 'life_%_recovered_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('base_mana_gained_on_enemy_death', 'number', 'modifier', 'mana_gained_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('mana_gained_on_enemy_death_per_level', 'number', 'modifier', 'mana_gained_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('recover_%_maximum_mana_on_kill', 'number', 'modifier', 'mana_%_recovered_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('maximum_mana_%_gained_on_kill', 'number', 'modifier', 'mana_%_recovered_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('base_energy_shield_gained_on_enemy_death', 'number', 'modifier', 'energy_shield_gained_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('energy_shield_gained_on_enemy_death_per_level', 'number', 'modifier', 'energy_shield_gained_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('recover_energy_shield_%_on_kill', 'number', 'modifier', 'energy_shield_%_recovered_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('life_gained_on_killing_ignited_enemies', 'number', 'modifier', 'life_gained_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('recover_%_maximum_life_on_killing_chilled_enemy', 'number', 'modifier', 'life_%_recovered_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('gain_X_energy_shield_on_killing_shocked_enemy', 'number', 'modifier', 'energy_shield_gained_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES ('recover_X_mana_on_killing_frozen_enemy', 'number', 'modifier', 'mana_gained_on_kill flat', 'KillRecovery.cs') ON CONFLICT(stat) DO UPDATE SET kind = 'number', handled = 'modifier', ref = excluded.ref;
