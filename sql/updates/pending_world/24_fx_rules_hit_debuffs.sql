-- Intimidate, Unnerve, Crush and Hinder on hit (HitDebuffs.cs) as fx rules. Every hit of the character's side counts
-- (who: any), as in the code. Where the code adds two stats and rolls once, the rule's chance is that sum: each term
-- is a stat, one of them only for an attack or only for a spell, and the hitting skill adds its own share. While a
-- rule is live for a character the code leaves its stats alone; a skill's share alone is still the code's.
-- Not here yet: Maim (its own immunity and avoidance), Blind, Exposure and Withered.
INSERT INTO fx_actions (id, takes, built, origin, note) VALUES
('debuff', 'debuff: intimidated, unnerved, crushed, hindered', 1, 'Intimidate Enemies for 4 seconds on Hit', 'Puts a timed debuff on the trigger''s enemy for the duration timer, from the moment of the hit; a later one restarts it.')
ON CONFLICT(id) DO NOTHING;
INSERT INTO fx_values (id, takes, built, origin, note) VALUES
('sum', 'of: a list of {stat, when}; skill: true to add the trigger''s skill''s own share of each', 1, 'chance to Intimidate on Hit plus the attacks-only chance', 'Several stats added up, for one roll. A term with "when" counts only for an event with that fact (attack, spell, critical). The rule is live while any of the stats is there; its sources list them, the first being the rule''s own.')
ON CONFLICT(id) DO NOTHING;
UPDATE fx_targets SET built = 1 WHERE id = 'enemy';
INSERT INTO fx_rules (id, book, title, replaces, status, origin) VALUES
('intimidate_on_hit', 'debuffs', 'Chance to Intimidate Enemies for 4 seconds on Hit; the attacks-only chance counts for an attack', 'chance_to_intimidate_on_hit_%', 'built', 'HitDebuffs.cs'),
('intimidate_on_spell_crit', 'debuffs', 'Spells inflict Intimidate on Critical Strike', 'spells_inflict_intimidate_on_crit', 'built', 'HitDebuffs.cs'),
('unnerve_on_hit', 'debuffs', 'Chance to Unnerve Enemies for 4 seconds on Hit; the spells-only chance counts for a spell', 'chance_to_unnerve_on_hit_%', 'built', 'HitDebuffs.cs'),
('unnerve_on_attack_crit', 'debuffs', 'Attacks inflict Unnerve on Critical Strike', 'attacks_inflict_unnerve_on_crit', 'built', 'HitDebuffs.cs'),
('crush_on_hit', 'debuffs', 'Chance to Crush Enemies for 4 seconds on Hit', 'chance_to_crush_on_hit_%', 'built', 'HitDebuffs.cs'),
('hinder_on_spell_hit', 'debuffs', 'Chance to Hinder Enemies on Hit with Spells', 'spells_chance_to_hinder_on_hit_%', 'built', 'HitDebuffs.cs');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('intimidate_on_hit', 0, 'stat', 'chance_to_intimidate_on_hit_%'),
('intimidate_on_hit', 1, 'stat', 'attacks_intimidate_on_hit_%'),
('intimidate_on_spell_crit', 0, 'stat', 'spells_inflict_intimidate_on_crit'),
('unnerve_on_hit', 0, 'stat', 'chance_to_unnerve_on_hit_%'),
('unnerve_on_hit', 1, 'stat', 'chance_to_unnerve_on_hit_with_spells_%'),
('unnerve_on_attack_crit', 0, 'stat', 'attacks_inflict_unnerve_on_crit'),
('crush_on_hit', 0, 'stat', 'chance_to_crush_on_hit_%'),
('hinder_on_spell_hit', 0, 'stat', 'spells_chance_to_hinder_on_hit_%');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('intimidate_on_hit', 0, 'hit', '{"who":"any"}'),
('intimidate_on_spell_crit', 0, 'hit', '{"who":"any"}'),
('unnerve_on_hit', 0, 'hit', '{"who":"any"}'),
('unnerve_on_attack_crit', 0, 'hit', '{"who":"any"}'),
('crush_on_hit', 0, 'hit', '{"who":"any"}'),
('hinder_on_spell_hit', 0, 'hit', '{"who":"any"}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('intimidate_on_hit', 0, 0, 'chance', 0, '{"percent":{"from":"sum","of":[{"stat":"chance_to_intimidate_on_hit_%"},{"stat":"attacks_intimidate_on_hit_%","when":"attack"}],"skill":true}}'),
('intimidate_on_spell_crit', 0, 0, 'skill_type', 0, '{"is":"spell"}'),
('intimidate_on_spell_crit', 0, 1, 'critical', 0, '{}'),
('unnerve_on_hit', 0, 0, 'chance', 0, '{"percent":{"from":"sum","of":[{"stat":"chance_to_unnerve_on_hit_%"},{"stat":"chance_to_unnerve_on_hit_with_spells_%","when":"spell"}],"skill":true}}'),
('unnerve_on_attack_crit', 0, 0, 'skill_type', 0, '{"is":"attack"}'),
('unnerve_on_attack_crit', 0, 1, 'critical', 0, '{}'),
('crush_on_hit', 0, 0, 'chance', 0, '{"percent":{"from":"roll","skill":true}}'),
('hinder_on_spell_hit', 0, 0, 'skill_type', 0, '{"is":"spell"}'),
('hinder_on_spell_hit', 0, 1, 'chance', 0, '{"percent":{"from":"roll","skill":true}}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('intimidate_on_hit', 0, 'debuff', '{"debuff":"intimidated"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('intimidate_on_spell_crit', 0, 'debuff', '{"debuff":"intimidated"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('unnerve_on_hit', 0, 'debuff', '{"debuff":"unnerved"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('unnerve_on_attack_crit', 0, 'debuff', '{"debuff":"unnerved"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('crush_on_hit', 0, 'debuff', '{"debuff":"crushed"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('hinder_on_spell_hit', 0, 'debuff', '{"debuff":"hindered"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]');
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES
('chance_to_intimidate_on_hit_%', 'trigger', 'rule', 'intimidate_on_hit', 'HitDebuffs.cs'),
('attacks_intimidate_on_hit_%', 'trigger', 'rule', 'intimidate_on_hit', 'HitDebuffs.cs'),
('spells_inflict_intimidate_on_crit', 'trigger', 'rule', 'intimidate_on_spell_crit', 'HitDebuffs.cs'),
('chance_to_unnerve_on_hit_%', 'trigger', 'rule', 'unnerve_on_hit', 'HitDebuffs.cs'),
('chance_to_unnerve_on_hit_with_spells_%', 'trigger', 'rule', 'unnerve_on_hit', 'HitDebuffs.cs'),
('attacks_inflict_unnerve_on_crit', 'trigger', 'rule', 'unnerve_on_attack_crit', 'HitDebuffs.cs'),
('chance_to_crush_on_hit_%', 'trigger', 'rule', 'crush_on_hit', 'HitDebuffs.cs'),
('spells_chance_to_hinder_on_hit_%', 'trigger', 'rule', 'hinder_on_spell_hit', 'HitDebuffs.cs')
ON CONFLICT(stat) DO UPDATE SET kind = 'trigger', handled = 'rule', ref = excluded.ref;
