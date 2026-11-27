-- Each actor rolls its eligible rules in explicit order within the hit event.
ALTER TABLE fx_rule_when ADD COLUMN roll_order INTEGER CHECK (roll_order > 0);

INSERT INTO fx_rules (id, book, title, replaces, status) VALUES
('frenzy_on_skill_hit', 'charges', 'Chance to gain a Frenzy Charge on skill hit', 'add_frenzy_charge_on_skill_hit_%', 'built'),
('power_on_hit', 'charges', 'Chance to gain a Power Charge on hit', 'add_power_charge_on_hit_%', 'built'),
('power_on_skill_hit', 'charges', 'Chance to gain a Power Charge on skill hit', 'add_power_charge_on_skill_hit_%', 'built');

INSERT INTO fx_rule_sources (rule, kind, key)
SELECT id, 'stat', replaces FROM fx_rules WHERE id IN ('frenzy_on_skill_hit', 'power_on_hit', 'power_on_skill_hit');

INSERT INTO fx_rule_when (rule, ord, trigger, args, roll_order) VALUES
('frenzy_on_skill_hit', 0, 'hit', '{}', 10),
('frenzy_on_skill_hit', 1, 'hit', '{"who":"monster"}', 10),
('power_on_hit', 0, 'hit', '{}', 20),
('power_on_skill_hit', 0, 'hit', '{}', 30),
('power_on_skill_hit', 1, 'hit', '{"who":"monster"}', 30);

INSERT INTO fx_rule_if (rule, condition, args)
SELECT id, 'chance', '{"percent":{"from":"roll"}}' FROM fx_rules
WHERE id IN ('frenzy_on_skill_hit', 'power_on_hit', 'power_on_skill_hit');

INSERT INTO fx_rule_do (rule, action, args) VALUES
('frenzy_on_skill_hit', 'charge', '{"charge":"frenzy"}'),
('power_on_hit', 'charge', '{"charge":"power"}'),
('power_on_skill_hit', 'charge', '{"charge":"power"}');
