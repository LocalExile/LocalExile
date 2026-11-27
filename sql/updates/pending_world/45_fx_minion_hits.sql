-- A minion's hit is a hit event with the minion as its actor ("who": "minion"). A rule whose trigger says so runs
-- for it; a rule for "any" actor means the character and what it places (totems, traps, mines), whose hits use the
-- character's stats, and does not run for minions. Raised at the minion's own hit (GolemCombat); a minion's
-- projectile and the other minion hit paths do not raise it yet.
--
-- Built with it from rows alone: "Minions have #% chance to Blind on Hit with Attacks" and "Minions have #% chance
-- to Hinder Enemies on Hit with Spells". In code beside it: "Minions' Attacks Overwhelm #% Physical Damage
-- Reduction".
UPDATE fx_triggers SET note = note || ' "who": "minion" makes it the hit of one of the actor''s minions; "any" is the actor and its totems, traps and mines, never its minions.'
WHERE id = 'hit';
INSERT INTO fx_rules (id, book, title, replaces, status, origin, note) VALUES
('minion_blind_on_attack_hit', 'minions', 'Minions have #% chance to Blind on Hit with Attacks', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before. Blind lasts 4 seconds, as the character''s own.'),
('minion_hinder_on_spell_hit', 'minions', 'Minions have #% chance to Hinder Enemies on Hit with Spells', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before. Hinder lasts 4 seconds, as the character''s own.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('minion_blind_on_attack_hit', 0, 'stat', 'minion_attacks_chance_to_blind_on_hit_%'),
('minion_hinder_on_spell_hit', 0, 'stat', 'minion_spells_chance_to_hinder_on_hit_%');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('minion_blind_on_attack_hit', 0, 'hit', '{"who":"minion"}'),
('minion_hinder_on_spell_hit', 0, 'hit', '{"who":"minion"}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('minion_blind_on_attack_hit', 0, 0, 'skill_type', 0, '{"is":"attack"}'),
('minion_blind_on_attack_hit', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}'),
('minion_hinder_on_spell_hit', 0, 0, 'skill_type', 0, '{"is":"spell"}'),
('minion_hinder_on_spell_hit', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('minion_blind_on_attack_hit', 0, 'debuff', '{"debuff":"blinded"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('minion_hinder_on_spell_hit', 0, 'debuff', '{"debuff":"hindered"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]');
UPDATE fx_stats SET handled = 'rule', ref = 'minion_blind_on_attack_hit', why = NULL, fit = NULL WHERE stat = 'minion_attacks_chance_to_blind_on_hit_%';
UPDATE fx_stats SET handled = 'rule', ref = 'minion_hinder_on_spell_hit', why = NULL, fit = NULL WHERE stat = 'minion_spells_chance_to_hinder_on_hit_%';
UPDATE fx_stats SET handled = 'code', code = 'GolemSkills.cs, GolemCombat.cs', why = NULL, fit = NULL WHERE stat = 'minions_attacks_overwhelm_%_physical_damage_reduction';
