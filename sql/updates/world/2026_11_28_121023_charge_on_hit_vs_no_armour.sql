-- Keep Ceaseless Feast in the same roll order as the C# hit rules.
INSERT INTO fx_rules (id, book, title, replaces, status, position)
VALUES ('endurance_charge_on_hit_%_vs_no_armour', 'charges', 'Chance to gain an Endurance Charge when you Hit an Enemy with no Armour',
        'endurance_charge_on_hit_%_vs_no_armour', 'built',
        (SELECT position + 1 FROM fx_rules WHERE id = 'chance_to_gain_endurance_charge_on_hit_%_vs_bleeding_enemy'));

INSERT INTO fx_rule_sources (rule, kind, key)
VALUES ('endurance_charge_on_hit_%_vs_no_armour', 'stat', 'endurance_charge_on_hit_%_vs_no_armour');

INSERT INTO fx_rule_when (rule, ord, trigger, args)
VALUES ('endurance_charge_on_hit_%_vs_no_armour', 0, 'hit', '{}');

INSERT INTO fx_rule_if (rule, ord, condition, args) VALUES
('endurance_charge_on_hit_%_vs_no_armour', 0, 'chance', '{"percent":{"from":"roll"}}'),
('endurance_charge_on_hit_%_vs_no_armour', 1, 'enemy_no_armour', '{}');

INSERT INTO fx_rule_do (rule, ord, action, args)
VALUES ('endurance_charge_on_hit_%_vs_no_armour', 0, 'charge', '{"charge":"endurance"}');
