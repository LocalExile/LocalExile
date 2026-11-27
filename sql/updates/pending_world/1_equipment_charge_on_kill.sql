INSERT INTO fx_rules (id, book, title, replaces, status) VALUES
('charge_on_kill_shield', 'items', 'Chance to gain an Endurance Charge on Kill while holding a Shield', 'endurance_charge_on_kill_percent_chance_while_holding_shield', 'built'),
('charge_on_kill_dual_wield', 'items', 'Chance to gain a Frenzy Charge on Kill while Dual Wielding', 'add_frenzy_charge_on_kill_%_chance_while_dual_wielding', 'built');

INSERT INTO fx_rule_sources (rule, kind, key) VALUES
('charge_on_kill_shield', 'stat', 'endurance_charge_on_kill_percent_chance_while_holding_shield'),
('charge_on_kill_dual_wield', 'stat', 'add_frenzy_charge_on_kill_%_chance_while_dual_wielding');

INSERT INTO fx_rule_when (rule, trigger) VALUES
('charge_on_kill_shield', 'kill'),
('charge_on_kill_dual_wield', 'kill');

INSERT INTO fx_rule_if (rule, condition, args) VALUES
('charge_on_kill_shield', 'chance', '{"percent":{"from":"roll"}}'),
('charge_on_kill_dual_wield', 'chance', '{"percent":{"from":"roll"}}');

INSERT INTO fx_rule_do (rule, action, args) VALUES
('charge_on_kill_shield', 'charge', '{"charge":"endurance"}'),
('charge_on_kill_dual_wield', 'charge', '{"charge":"frenzy"}');

INSERT INTO fx_rule_if (rule, ord, condition, args) VALUES
('charge_on_kill_shield', 1, 'equipment', '{"is":"holding_shield"}'),
('charge_on_kill_dual_wield', 1, 'equipment', '{"is":"dual_wielding"}');
