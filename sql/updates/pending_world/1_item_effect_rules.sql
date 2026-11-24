-- Keep the original independent Onslaught rolls in the same order, including the duration override.
INSERT INTO item_effect_rules (id, stat, event, action, chance_from_stat, duration_ms, duration_stat) VALUES
(1, 'chance_to_gain_onslaught_on_kill_%', 'kill', 'onslaught', 1, 4000, 'onslaught_time_granted_on_kill_ms'),
(2, 'onslaught_buff_duration_on_kill_ms', 'kill', 'onslaught', 0, 0, 'onslaught_buff_duration_on_kill_ms'),
(3, 'chance_to_gain_onslaught_on_kill_for_4_seconds_%', 'kill', 'onslaught', 1, 4000, NULL),
(4, 'chance_to_gain_onslaught_on_kill_for_10_seconds_%', 'kill', 'onslaught', 1, 10000, NULL);

-- The grant supplies its own mark, level, cost and cooldown from the client tables.
INSERT INTO item_effect_rules (id, stat, event, action, rare_or_unique, requires_no_mark) VALUES
(5, 'trigger_on_hit_against_rare_or_unique_if_no_marked_enemy', 'hit', 'cast_granted_mark', 1, 1);
