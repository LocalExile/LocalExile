-- Limits an action can be told to ignore, by name: an action's "ignore" argument lists them (several with commas).
-- built = 1: the action that owns the limit honours the name today.
CREATE TABLE fx_limits (
    id TEXT PRIMARY KEY,
    action TEXT NOT NULL,
    built INTEGER NOT NULL DEFAULT 0 CHECK (built IN (0, 1)),
    origin TEXT,
    note TEXT NOT NULL
) STRICT;
INSERT INTO fx_limits (id, action, built, origin, note) VALUES
('curse_limit', 'curse', 1, 'Ylfeban''s Trickery ("ignoring Curse Limit")', 'The hex takes a place of its own on the enemy beyond the actor''s curse limit.'),
('maximum_charges', 'charge', 0, '"can gain charges beyond the maximum" lines', 'A gained charge is kept above the actor''s maximum for that charge.'),
('buff_stacks', 'buff', 0, 'stacking buffs with a maximum number of stacks', 'A stack is added above the buff''s own maximum.'),
('cooldown', 'skill', 0, '"ignores cooldown" triggers', 'The triggered skill is released whether or not its cooldown has a use left.'),
('maximum_resistance', 'modifier', 0, '"ignoring maximum resistances" lines', 'A resistance granted this way is not held to the maximum resistance.');

-- "#% chance to Curse Enemies which Hit you with a random Hex, ignoring Curse Limit" (Ylfeban's Trickery): a rule of
-- parts in place of the code of update 69. "curse":"random" is one of all nine hexes.
INSERT INTO fx_rules (id, book, title, replaces, status, origin, note) VALUES
('random_hex_when_hit', 'items', 'Curse Enemies which Hit you with a random Hex, ignoring Curse Limit', NULL, 'built', 'parts', 'Ylfeban''s Trickery. Level 1.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES ('random_hex_when_hit', 0, 'stat', 'random_curse_when_hit_%_ignoring_curse_limit');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES ('random_hex_when_hit', 0, 'when_hit', '{}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('random_hex_when_hit', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('random_hex_when_hit', 0, 'curse', '{"curse":"random","ignore":"curse_limit","refresh_after":0}', 'enemy', '{}', '{"fixed":1}', '[]');
UPDATE fx_stats SET handled = 'rule', ref = 'random_hex_when_hit', why = NULL, fit = NULL
WHERE stat = 'random_curse_when_hit_%_ignoring_curse_limit';

-- Played in the client by the owner on 2026-11-25 14:07 and working.
UPDATE fx_stats SET checked = 1 WHERE stat IN ('local_weapon_crit_chance_is_100',
  'unique_helmet_cast_speed_+%_applies_to_attack_speed_at_%_of_original_value', 'maximum_void_arrows',
  'grant_void_arrow_every_x_ms', 'local_display_trigger_level_x_void_shot_on_arrow_fire_while_you_have_void_arrow');
UPDATE fx_rules SET checked = 1 WHERE id = 'trigger_on_skill_use_%_if_you_have_a_void_arrow';
UPDATE effect_skills SET checked = 1 WHERE effect = 'VoidShot';
