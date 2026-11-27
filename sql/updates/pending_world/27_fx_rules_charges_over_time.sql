-- Charges over time (ChargeGains.Periodics, 14 stats) as fx rules on the new "interval" trigger. A timed rule is not
-- an event rule: the charge system keeps the timers (game clock, alive, outside towns; a conditional timer starts
-- again when its condition stops holding) and reads its list from these rows while the effect rules are on, and from
-- its own list otherwise. A stat keeps its timer's place either way. The period and the count are fixed or the
-- stat's own value ("from":"roll", with a scale).
UPDATE fx_triggers SET built = 1, takes = 'seconds: how often, fixed or the stat''s own value ({"from":"roll"}, with a "scale")',
    note = 'Every so many seconds of game time while the rule''s stat is there and its condition holds. Built for rules that give or take the actor''s charges: the charge system keeps the timers.'
WHERE id = 'interval';
UPDATE fx_conditions SET takes = takes || '; a timed rule knows: stationary, moving' WHERE id = 'self_state';
UPDATE fx_conditions SET takes = takes || '; a timed rule knows the events been_hit, lost_charge (of the rule''s charge type) and detonated_mines' WHERE id = 'recently';
INSERT INTO fx_rules (id, book, title, replaces, status, origin) VALUES
('endurance_charges_each_second', 'charges', 'Gain Endurance Charges each second (a negative value loses them)', 'gain_X_endurance_charges_per_second', 'built', 'ChargeGains.cs'),
('frenzy_charges_each_second', 'charges', 'Gain Frenzy Charges each second (a negative value loses them)', 'gain_X_frenzy_charges_per_second', 'built', 'ChargeGains.cs'),
('power_charges_each_second', 'charges', 'Gain Power Charges each second (a negative value loses them)', 'gain_X_power_charges_per_second', 'built', 'ChargeGains.cs'),
('endurance_charge_every_x_ms', 'charges', 'Gain an Endurance Charge every X seconds (the body armour implicit)', 'body_armour_implicit_gain_endurance_charge_every_x_ms', 'built', 'ChargeGains.cs'),
('frenzy_charge_every_x_ms', 'charges', 'Gain a Frenzy Charge every X seconds (the body armour implicit)', 'body_armour_implicit_gain_frenzy_charge_every_x_ms', 'built', 'ChargeGains.cs'),
('power_charge_every_x_ms', 'charges', 'Gain a Power Charge every X seconds (the body armour implicit)', 'body_armour_implicit_gain_power_charge_every_x_ms', 'built', 'ChargeGains.cs'),
('random_charges_every_6_seconds', 'charges', 'Gain an Endurance, Frenzy or Power Charge every 6 seconds', 'gain_X_random_charges_every_6_seconds', 'built', 'ChargeGains.cs'),
('endurance_charge_each_second_if_hit_recently', 'charges', 'Gain an Endurance Charge every second if you''ve been Hit Recently', 'gain_endurance_charge_per_second_if_have_been_hit_recently', 'built', 'ChargeGains.cs'),
('endurance_charges_every_4_seconds_stationary', 'charges', 'Gain Endurance Charges every 4 seconds while Stationary', 'number_of_endurance_charges_to_gain_every_4_seconds_while_stationary', 'built', 'ChargeGains.cs'),
('endurance_charge_every_x_seconds_stationary', 'charges', 'Gain an Endurance Charge every X seconds while Stationary', 'gain_a_endurance_charge_every_X_seconds_while_stationary', 'built', 'ChargeGains.cs'),
('random_charge_each_second_stationary', 'charges', 'Gain a random Charge every second while Stationary', 'gain_random_charge_per_second_while_stationary', 'built', 'ChargeGains.cs'),
('frenzy_charge_every_x_seconds_moving', 'charges', 'Gain a Frenzy Charge every X seconds while Moving', 'gain_a_frenzy_charge_every_X_seconds_while_moving', 'built', 'ChargeGains.cs'),
('power_charge_each_second_if_none_lost_recently', 'charges', 'Gain a Power Charge every Second if you haven''t lost Power Charges Recently', 'gain_power_charge_per_second_if_have_not_lost_power_charge_recently', 'built', 'ChargeGains.cs'),
('lose_power_charge_each_second_without_mines', 'charges', 'Lose a Power Charge each second if you have not Detonated Mines Recently', 'lose_power_charge_each_second_if_not_detonated_mines_recently', 'built', 'ChargeGains.cs');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('endurance_charges_each_second', 0, 'stat', 'gain_X_endurance_charges_per_second'),
('frenzy_charges_each_second', 0, 'stat', 'gain_X_frenzy_charges_per_second'),
('power_charges_each_second', 0, 'stat', 'gain_X_power_charges_per_second'),
('endurance_charge_every_x_ms', 0, 'stat', 'body_armour_implicit_gain_endurance_charge_every_x_ms'),
('frenzy_charge_every_x_ms', 0, 'stat', 'body_armour_implicit_gain_frenzy_charge_every_x_ms'),
('power_charge_every_x_ms', 0, 'stat', 'body_armour_implicit_gain_power_charge_every_x_ms'),
('random_charges_every_6_seconds', 0, 'stat', 'gain_X_random_charges_every_6_seconds'),
('endurance_charge_each_second_if_hit_recently', 0, 'stat', 'gain_endurance_charge_per_second_if_have_been_hit_recently'),
('endurance_charges_every_4_seconds_stationary', 0, 'stat', 'number_of_endurance_charges_to_gain_every_4_seconds_while_stationary'),
('endurance_charge_every_x_seconds_stationary', 0, 'stat', 'gain_a_endurance_charge_every_X_seconds_while_stationary'),
('random_charge_each_second_stationary', 0, 'stat', 'gain_random_charge_per_second_while_stationary'),
('frenzy_charge_every_x_seconds_moving', 0, 'stat', 'gain_a_frenzy_charge_every_X_seconds_while_moving'),
('power_charge_each_second_if_none_lost_recently', 0, 'stat', 'gain_power_charge_per_second_if_have_not_lost_power_charge_recently'),
('lose_power_charge_each_second_without_mines', 0, 'stat', 'lose_power_charge_each_second_if_not_detonated_mines_recently');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('endurance_charges_each_second', 0, 'interval', '{"seconds":{"fixed":1}}'),
('frenzy_charges_each_second', 0, 'interval', '{"seconds":{"fixed":1}}'),
('power_charges_each_second', 0, 'interval', '{"seconds":{"fixed":1}}'),
('endurance_charge_every_x_ms', 0, 'interval', '{"seconds":{"from":"roll","scale":0.001}}'),
('frenzy_charge_every_x_ms', 0, 'interval', '{"seconds":{"from":"roll","scale":0.001}}'),
('power_charge_every_x_ms', 0, 'interval', '{"seconds":{"from":"roll","scale":0.001}}'),
('random_charges_every_6_seconds', 0, 'interval', '{"seconds":{"fixed":6}}'),
('endurance_charge_each_second_if_hit_recently', 0, 'interval', '{"seconds":{"fixed":1}}'),
('endurance_charges_every_4_seconds_stationary', 0, 'interval', '{"seconds":{"fixed":4}}'),
('endurance_charge_every_x_seconds_stationary', 0, 'interval', '{"seconds":{"from":"roll"}}'),
('random_charge_each_second_stationary', 0, 'interval', '{"seconds":{"fixed":1}}'),
('frenzy_charge_every_x_seconds_moving', 0, 'interval', '{"seconds":{"from":"roll"}}'),
('power_charge_each_second_if_none_lost_recently', 0, 'interval', '{"seconds":{"fixed":1}}'),
('lose_power_charge_each_second_without_mines', 0, 'interval', '{"seconds":{"fixed":1}}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('endurance_charge_each_second_if_hit_recently', 0, 0, 'recently', 0, '{"event":"been_hit"}'),
('endurance_charges_every_4_seconds_stationary', 0, 0, 'self_state', 0, '{"is":"stationary"}'),
('endurance_charge_every_x_seconds_stationary', 0, 0, 'self_state', 0, '{"is":"stationary"}'),
('random_charge_each_second_stationary', 0, 0, 'self_state', 0, '{"is":"stationary"}'),
('frenzy_charge_every_x_seconds_moving', 0, 0, 'self_state', 0, '{"is":"moving"}'),
('power_charge_each_second_if_none_lost_recently', 0, 0, 'recently', 1, '{"event":"lost_charge"}'),
('lose_power_charge_each_second_without_mines', 0, 0, 'recently', 1, '{"event":"detonated_mines"}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('endurance_charges_each_second', 0, 'charge', '{"type":"endurance"}', 'self', '{}', '{"from":"roll"}', '[]'),
('frenzy_charges_each_second', 0, 'charge', '{"type":"frenzy"}', 'self', '{}', '{"from":"roll"}', '[]'),
('power_charges_each_second', 0, 'charge', '{"type":"power"}', 'self', '{}', '{"from":"roll"}', '[]'),
('endurance_charge_every_x_ms', 0, 'charge', '{"type":"endurance"}', 'self', '{}', '{"fixed":1}', '[]'),
('frenzy_charge_every_x_ms', 0, 'charge', '{"type":"frenzy"}', 'self', '{}', '{"fixed":1}', '[]'),
('power_charge_every_x_ms', 0, 'charge', '{"type":"power"}', 'self', '{}', '{"fixed":1}', '[]'),
('random_charges_every_6_seconds', 0, 'charge', '{"type":"random"}', 'self', '{}', '{"from":"roll"}', '[]'),
('endurance_charge_each_second_if_hit_recently', 0, 'charge', '{"type":"endurance"}', 'self', '{}', '{"from":"roll"}', '[]'),
('endurance_charges_every_4_seconds_stationary', 0, 'charge', '{"type":"endurance"}', 'self', '{}', '{"from":"roll"}', '[]'),
('endurance_charge_every_x_seconds_stationary', 0, 'charge', '{"type":"endurance"}', 'self', '{}', '{"fixed":1}', '[]'),
('random_charge_each_second_stationary', 0, 'charge', '{"type":"random"}', 'self', '{}', '{"from":"roll"}', '[]'),
('frenzy_charge_every_x_seconds_moving', 0, 'charge', '{"type":"frenzy"}', 'self', '{}', '{"fixed":1}', '[]'),
('power_charge_each_second_if_none_lost_recently', 0, 'charge', '{"type":"power"}', 'self', '{}', '{"from":"roll"}', '[]'),
('lose_power_charge_each_second_without_mines', 0, 'charge', '{"type":"power","does":"lose_one"}', 'self', '{}', '{}', '[]');
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES
('gain_X_endurance_charges_per_second', 'trigger', 'rule', 'endurance_charges_each_second', 'ChargeGains.cs'),
('gain_X_frenzy_charges_per_second', 'trigger', 'rule', 'frenzy_charges_each_second', 'ChargeGains.cs'),
('gain_X_power_charges_per_second', 'trigger', 'rule', 'power_charges_each_second', 'ChargeGains.cs'),
('body_armour_implicit_gain_endurance_charge_every_x_ms', 'trigger', 'rule', 'endurance_charge_every_x_ms', 'ChargeGains.cs'),
('body_armour_implicit_gain_frenzy_charge_every_x_ms', 'trigger', 'rule', 'frenzy_charge_every_x_ms', 'ChargeGains.cs'),
('body_armour_implicit_gain_power_charge_every_x_ms', 'trigger', 'rule', 'power_charge_every_x_ms', 'ChargeGains.cs'),
('gain_X_random_charges_every_6_seconds', 'trigger', 'rule', 'random_charges_every_6_seconds', 'ChargeGains.cs'),
('gain_endurance_charge_per_second_if_have_been_hit_recently', 'trigger', 'rule', 'endurance_charge_each_second_if_hit_recently', 'ChargeGains.cs'),
('number_of_endurance_charges_to_gain_every_4_seconds_while_stationary', 'trigger', 'rule', 'endurance_charges_every_4_seconds_stationary', 'ChargeGains.cs'),
('gain_a_endurance_charge_every_X_seconds_while_stationary', 'trigger', 'rule', 'endurance_charge_every_x_seconds_stationary', 'ChargeGains.cs'),
('gain_random_charge_per_second_while_stationary', 'trigger', 'rule', 'random_charge_each_second_stationary', 'ChargeGains.cs'),
('gain_a_frenzy_charge_every_X_seconds_while_moving', 'trigger', 'rule', 'frenzy_charge_every_x_seconds_moving', 'ChargeGains.cs'),
('gain_power_charge_per_second_if_have_not_lost_power_charge_recently', 'trigger', 'rule', 'power_charge_each_second_if_none_lost_recently', 'ChargeGains.cs'),
('lose_power_charge_each_second_if_not_detonated_mines_recently', 'trigger', 'rule', 'lose_power_charge_each_second_without_mines', 'ChargeGains.cs')
ON CONFLICT(stat) DO UPDATE SET kind = 'trigger', handled = 'rule', ref = excluded.ref;
