CREATE TABLE fx_rules (
    id TEXT PRIMARY KEY,
    book TEXT NOT NULL,
    title TEXT NOT NULL,
    replaces TEXT,
    status TEXT NOT NULL DEFAULT 'not built' CHECK (status IN ('built', 'partial', 'not built')),
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    checked INTEGER NOT NULL DEFAULT 0 CHECK (checked IN (0, 1)),
    origin TEXT,
    note TEXT
) STRICT;

CREATE TABLE fx_rule_sources (
    rule TEXT NOT NULL REFERENCES fx_rules(id) ON DELETE CASCADE,
    ord INTEGER NOT NULL DEFAULT 0,
    kind TEXT NOT NULL CHECK (kind IN ('stat', 'skill')),
    key TEXT NOT NULL DEFAULT '',
    PRIMARY KEY (rule, ord)
) STRICT;

CREATE TABLE fx_rule_when (
    rule TEXT NOT NULL REFERENCES fx_rules(id) ON DELETE CASCADE,
    ord INTEGER NOT NULL DEFAULT 0,
    trigger TEXT NOT NULL,
    args TEXT NOT NULL DEFAULT '{}',
    PRIMARY KEY (rule, ord)
) STRICT;

CREATE TABLE fx_rule_if (
    rule TEXT NOT NULL REFERENCES fx_rules(id) ON DELETE CASCADE,
    grp INTEGER NOT NULL DEFAULT 0,
    ord INTEGER NOT NULL DEFAULT 0,
    condition TEXT NOT NULL,
    negate INTEGER NOT NULL DEFAULT 0 CHECK (negate IN (0, 1)),
    args TEXT NOT NULL DEFAULT '{}',
    PRIMARY KEY (rule, grp, ord)
) STRICT;

CREATE TABLE fx_rule_do (
    rule TEXT NOT NULL REFERENCES fx_rules(id) ON DELETE CASCADE,
    ord INTEGER NOT NULL DEFAULT 0,
    action TEXT NOT NULL,
    args TEXT NOT NULL DEFAULT '{}',
    target TEXT NOT NULL DEFAULT 'self',
    target_args TEXT NOT NULL DEFAULT '{}',
    value TEXT NOT NULL DEFAULT '{}',
    timers TEXT NOT NULL DEFAULT '[]',
    PRIMARY KEY (rule, ord)
) STRICT;

INSERT INTO fx_rules (id, book, title, replaces, status) VALUES
('onslaught_1_chance', 'items', '% chance to gain Onslaught for 4 seconds on Kill; onslaught_time_granted_on_kill_ms replaces the 4 seconds', 'chance_to_gain_onslaught_on_kill_%', 'built'),
('onslaught_4_ten_seconds', 'items', '% chance to gain Onslaught for 10 seconds on Kill', 'chance_to_gain_onslaught_on_kill_for_10_seconds_%', 'built'),
('onslaught_3_four_seconds', 'items', '% chance to gain Onslaught for 4 seconds on Kill', 'chance_to_gain_onslaught_on_kill_for_4_seconds_%', 'built'),
('onslaught_2_duration', 'items', 'You gain Onslaught for X seconds on Kill', 'onslaught_buff_duration_on_kill_ms', 'built'),
('granted_mark', 'items', 'Trigger Level X <Mark> when you Hit a Rare or Unique Enemy and have no Mark', NULL, 'built'),
('socketed_spell_on_block', 'items', 'Trigger a supported spell socketed in the blocking item', NULL, 'built');

INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('onslaught_1_chance', 0, 'stat', 'chance_to_gain_onslaught_on_kill_%'),
('onslaught_4_ten_seconds', 0, 'stat', 'chance_to_gain_onslaught_on_kill_for_10_seconds_%'),
('onslaught_3_four_seconds', 0, 'stat', 'chance_to_gain_onslaught_on_kill_for_4_seconds_%'),
('onslaught_2_duration', 0, 'stat', 'onslaught_buff_duration_on_kill_ms'),
('granted_mark', 0, 'skill', 'trigger_on_hit_against_rare_or_unique_if_no_marked_enemy'),
('onslaught_2_duration', 1, 'stat', 'gain_onslaught_on_kill_ms_while_affected_by_haste'),
('socketed_spell_on_block', 0, 'skill', 'trigger_effect_group_on_block');

INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('onslaught_1_chance', 0, 'kill', '{}'),
('onslaught_4_ten_seconds', 0, 'kill', '{}'),
('onslaught_3_four_seconds', 0, 'kill', '{}'),
('onslaught_2_duration', 0, 'kill', '{}'),
('granted_mark', 0, 'hit', '{}'),
('socketed_spell_on_block', 0, 'block', '{}');

INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('onslaught_1_chance', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('onslaught_4_ten_seconds', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('onslaught_3_four_seconds', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('granted_mark', 0, 0, 'enemy_rarity', 0, '{"is":"rare_or_unique"}'),
('granted_mark', 0, 1, 'has_mark', 1, '{}'),
('granted_mark', 0, 2, 'cooldown', 0, '{"seconds":{"from":"source","name":"cooldown"}}');

INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('onslaught_1_chance', 0, 'buff', '{"buff":"onslaught"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"from":"stat","stat":"onslaught_time_granted_on_kill_ms","scale":0.001,"fallback":4}}]'),
('onslaught_4_ten_seconds', 0, 'buff', '{"buff":"onslaught"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":10}}]'),
('onslaught_3_four_seconds', 0, 'buff', '{"buff":"onslaught"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('onslaught_2_duration', 0, 'buff', '{"buff":"onslaught"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"from":"roll","scale":0.001}}]'),
('granted_mark', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]'),
('socketed_spell_on_block', 0, 'skill', '{"skill":"supported_socketed"}', 'attacker', '{}', '{}', '[]');
