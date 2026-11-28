-- The additional charges on kill, at their old place between the chance rows (-10) and the conditional ones (10).

INSERT INTO fx_rules (id, book, title, replaces, status, position) VALUES
('add_endurance_charge_on_kill', 'charges', 'Gain an Endurance Charge on Kill', 'add_endurance_charge_on_kill', 'built', 2),
('add_frenzy_charge_on_kill', 'charges', 'Gain a Frenzy Charge on Kill', 'add_frenzy_charge_on_kill', 'built', 4),
('power_frenzy_or_endurance_charge_on_kill_%', 'charges', 'Chance to gain a Power, Frenzy or Endurance Charge on Kill', 'power_frenzy_or_endurance_charge_on_kill_%', 'built', 6);

INSERT INTO fx_rule_sources (rule, kind, key)
SELECT id, 'stat', replaces FROM fx_rules WHERE id IN ('add_endurance_charge_on_kill', 'add_frenzy_charge_on_kill', 'power_frenzy_or_endurance_charge_on_kill_%');

INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('add_endurance_charge_on_kill', 0, 'kill', '{}'),
('add_frenzy_charge_on_kill', 0, 'kill', '{}'),
('power_frenzy_or_endurance_charge_on_kill_%', 0, 'kill', '{}');

INSERT INTO fx_rule_if (rule, ord, condition, negate, args) VALUES
('power_frenzy_or_endurance_charge_on_kill_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}');

INSERT INTO fx_rule_do (rule, ord, action, args) VALUES
('add_endurance_charge_on_kill', 0, 'charge', '{"charge":"endurance"}'),
('add_frenzy_charge_on_kill', 0, 'charge', '{"charge":"frenzy"}'),
('power_frenzy_or_endurance_charge_on_kill_%', 0, 'charge', '{"charge":"random"}');
