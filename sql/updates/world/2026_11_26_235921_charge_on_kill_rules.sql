INSERT INTO fx_rules (id, book, title, replaces, status) VALUES
('charge_on_kill_endurance', 'items', 'Chance to gain an Endurance Charge on Kill', 'endurance_charge_on_kill_%', 'built'),
('charge_on_kill_frenzy', 'items', 'Chance to gain a Frenzy Charge on Kill', 'add_frenzy_charge_on_kill_%_chance', 'built'),
('charge_on_kill_power', 'items', 'Chance to gain a Power Charge on Kill', 'add_power_charge_on_kill_%_chance', 'built');

INSERT INTO fx_rule_sources (rule, kind, key) VALUES
('charge_on_kill_endurance', 'stat', 'endurance_charge_on_kill_%'),
('charge_on_kill_frenzy', 'stat', 'add_frenzy_charge_on_kill_%_chance'),
('charge_on_kill_power', 'stat', 'add_power_charge_on_kill_%_chance');

INSERT INTO fx_rule_when (rule, trigger) VALUES
('charge_on_kill_endurance', 'kill'),
('charge_on_kill_frenzy', 'kill'),
('charge_on_kill_power', 'kill');

INSERT INTO fx_rule_if (rule, condition, args) VALUES
('charge_on_kill_endurance', 'chance', '{"percent":{"from":"roll"}}'),
('charge_on_kill_frenzy', 'chance', '{"percent":{"from":"roll"}}'),
('charge_on_kill_power', 'chance', '{"percent":{"from":"roll"}}');

INSERT INTO fx_rule_do (rule, action, args) VALUES
('charge_on_kill_endurance', 'charge', '{"charge":"endurance"}'),
('charge_on_kill_frenzy', 'charge', '{"charge":"frenzy"}'),
('charge_on_kill_power', 'charge', '{"charge":"power"}');
