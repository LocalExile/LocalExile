-- Rule ids sort after equipment checks, with Power before Frenzy as in the original kill rolls.
INSERT INTO fx_rules (id, book, title, replaces, status) VALUES
('charge_on_kill_vs_frozen_1_power', 'items', 'Chance to gain a Power Charge on killing a Frozen Enemy', 'chance_to_gain_power_charge_on_killing_frozen_enemy_%', 'built'),
('charge_on_kill_vs_frozen_2_frenzy', 'items', 'Chance to gain a Frenzy Charge on killing a Frozen Enemy', 'chance_to_gain_frenzy_charge_on_killing_frozen_enemy_%', 'built');

INSERT INTO fx_rule_sources (rule, kind, key) VALUES
('charge_on_kill_vs_frozen_1_power', 'stat', 'chance_to_gain_power_charge_on_killing_frozen_enemy_%'),
('charge_on_kill_vs_frozen_2_frenzy', 'stat', 'chance_to_gain_frenzy_charge_on_killing_frozen_enemy_%');

INSERT INTO fx_rule_when (rule, trigger) VALUES
('charge_on_kill_vs_frozen_1_power', 'kill'),
('charge_on_kill_vs_frozen_2_frenzy', 'kill');

INSERT INTO fx_rule_if (rule, ord, condition, args) VALUES
('charge_on_kill_vs_frozen_1_power', 0, 'chance', '{"percent":{"from":"roll"}}'),
('charge_on_kill_vs_frozen_1_power', 1, 'enemy_frozen_at_death', '{}'),
('charge_on_kill_vs_frozen_2_frenzy', 0, 'chance', '{"percent":{"from":"roll"}}'),
('charge_on_kill_vs_frozen_2_frenzy', 1, 'enemy_frozen_at_death', '{}');

INSERT INTO fx_rule_do (rule, action, args) VALUES
('charge_on_kill_vs_frozen_1_power', 'charge', '{"charge":"power"}'),
('charge_on_kill_vs_frozen_2_frenzy', 'charge', '{"charge":"frenzy"}');
