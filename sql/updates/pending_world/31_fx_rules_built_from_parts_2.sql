-- Seven more stats built from parts alone: Punishment on hit, Enfeeble on hit against enemies that are not cursed,
-- Vulnerability on block, Chaotic Might on kill and on critical strike, and Covered in Ash on the monster that hits
-- you. For these the curse action also curses an attacker (when hit, on block), the debuff action knows
-- covered_in_ash, and a rule on when_hit or block can ask the attacker's rarity.
UPDATE fx_actions SET takes = replace(takes, 'lightning_exposure;', 'lightning_exposure, covered_in_ash;') WHERE id = 'debuff';
UPDATE fx_actions SET note = note || ' On when_hit and block the enemy is the attacker.' WHERE id = 'curse';
UPDATE fx_conditions SET note = note || ' On when_hit and block it is the attacker''s rarity.' WHERE id = 'enemy_rarity';
INSERT INTO fx_rules (id, book, title, replaces, status, origin, note) VALUES
('curse_punishment_on_hit_chance', 'curses', 'Chance to Curse Enemies with Punishment on Hit', NULL, 'built', 'parts', 'Built from parts alone: no code handled this stat before.'),
('curse_enfeeble_on_hit_vs_uncursed', 'curses', 'Curse Non-Cursed Enemies with Enfeeble on Hit', NULL, 'built', 'parts', 'Built from parts alone: no code handled this stat before.'),
('curse_vulnerability_on_block', 'curses', 'Curse Enemies with level 5 Vulnerability on Block', NULL, 'built', 'parts', 'Built from parts alone: no code handled this stat before.'),
('chaotic_might_on_kill', 'buffs', 'Chance to gain Chaotic Might for 10 seconds on Kill', NULL, 'built', 'parts', 'Built from parts alone: no code handled this stat before.'),
('chaotic_might_on_crit', 'buffs', 'Gain Chaotic Might for 4 seconds on Critical Strike', NULL, 'built', 'parts', 'Built from parts alone: no code handled this stat before.'),
('ash_attacker_when_hit', 'debuffs', 'Chance to cover Enemies in Ash when they Hit you', NULL, 'built', 'parts', 'Built from parts alone: no code handled this stat before.'),
('ash_rare_attacker_when_hit', 'debuffs', 'Chance to Cover Rare or Unique Enemies in Ash for 10 Seconds when they Hit you', NULL, 'built', 'parts', 'Built from parts alone: no code handled this stat before.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('curse_punishment_on_hit_chance', 0, 'stat', 'curse_with_punishment_on_hit_%'),
('curse_enfeeble_on_hit_vs_uncursed', 0, 'stat', 'curse_with_enfeeble_on_hit_%_against_uncursed_enemies'),
('curse_vulnerability_on_block', 0, 'stat', 'curse_on_block_level_5_vulnerability'),
('chaotic_might_on_kill', 0, 'stat', 'chance_to_gain_old_unholy_might_on_kill_for_10_seconds_%'),
('chaotic_might_on_crit', 0, 'stat', 'chance_to_gain_chaotic_might_on_crit_for_4_seconds_%'),
('ash_attacker_when_hit', 0, 'stat', 'apply_covered_in_ash_to_attacker_when_hit_%'),
('ash_rare_attacker_when_hit', 0, 'stat', 'apply_covered_in_ash_to_attacker_on_hit_%_vs_rare_or_unique_enemy');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('curse_punishment_on_hit_chance', 0, 'hit', '{}'),
('curse_enfeeble_on_hit_vs_uncursed', 0, 'hit', '{}'),
('curse_vulnerability_on_block', 0, 'block', '{}'),
('chaotic_might_on_kill', 0, 'kill', '{}'),
('chaotic_might_on_crit', 0, 'hit', '{}'),
('ash_attacker_when_hit', 0, 'when_hit', '{}'),
('ash_rare_attacker_when_hit', 0, 'when_hit', '{}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('curse_punishment_on_hit_chance', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('curse_enfeeble_on_hit_vs_uncursed', 0, 0, 'enemy_state', 1, '{"is":"cursed"}'),
('curse_enfeeble_on_hit_vs_uncursed', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chaotic_might_on_kill', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chaotic_might_on_crit', 0, 0, 'critical', 0, '{}'),
('chaotic_might_on_crit', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}'),
('ash_attacker_when_hit', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('ash_rare_attacker_when_hit', 0, 0, 'enemy_rarity', 0, '{"is":"rare_or_unique"}'),
('ash_rare_attacker_when_hit', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('curse_punishment_on_hit_chance', 0, 'curse', '{"curse":"Punishment"}', 'enemy', '{}', '{"fixed":1}', '[]'),
('curse_enfeeble_on_hit_vs_uncursed', 0, 'curse', '{"curse":"Enfeeble"}', 'enemy', '{}', '{"fixed":1}', '[]'),
('curse_vulnerability_on_block', 0, 'curse', '{"curse":"Vulnerability"}', 'enemy', '{}', '{"fixed":5}', '[]'),
('chaotic_might_on_kill', 0, 'buff', '{"buff":"old_unholy_might","values":[30]}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":10}}]'),
('chaotic_might_on_crit', 0, 'buff', '{"buff":"old_unholy_might","values":[30]}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('ash_attacker_when_hit', 0, 'debuff', '{"debuff":"covered_in_ash"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('ash_rare_attacker_when_hit', 0, 'debuff', '{"debuff":"covered_in_ash"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":10}}]');
INSERT INTO fx_stats (stat, kind, handled, ref) VALUES
('curse_with_punishment_on_hit_%', 'trigger', 'rule', 'curse_punishment_on_hit_chance'),
('curse_with_enfeeble_on_hit_%_against_uncursed_enemies', 'trigger', 'rule', 'curse_enfeeble_on_hit_vs_uncursed'),
('curse_on_block_level_5_vulnerability', 'trigger', 'rule', 'curse_vulnerability_on_block'),
('chance_to_gain_old_unholy_might_on_kill_for_10_seconds_%', 'trigger', 'rule', 'chaotic_might_on_kill'),
('chance_to_gain_chaotic_might_on_crit_for_4_seconds_%', 'trigger', 'rule', 'chaotic_might_on_crit'),
('apply_covered_in_ash_to_attacker_when_hit_%', 'trigger', 'rule', 'ash_attacker_when_hit'),
('apply_covered_in_ash_to_attacker_on_hit_%_vs_rare_or_unique_enemy', 'trigger', 'rule', 'ash_rare_attacker_when_hit')
ON CONFLICT(stat) DO UPDATE SET handled = 'rule', ref = excluded.ref;
