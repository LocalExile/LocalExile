-- Append to the existing kill-roll sequence, including any custom rules.
WITH last_roll AS (SELECT max(0, coalesce(max(kill_charge_order), 0)) AS position FROM fx_rules),
new_rules(id, title, stat, offset) AS (VALUES
    ('maligaro_poison_frenzy', 'Chance to gain a Frenzy Charge on killing an enemy with at least 5 Poisons', 'gain_frenzy_charge_on_kill_vs_enemies_with_5+_poisons_%', 10),
    ('maligaro_poison_power', 'Chance to gain a Power Charge on killing an enemy with fewer than 5 Poisons', 'gain_power_charge_on_kill_vs_enemies_with_less_than_5_poisons_%', 20)
)
INSERT INTO fx_rules (id, book, title, replaces, status, kill_charge_order)
SELECT id, 'items', title, stat, 'built', position + offset FROM new_rules CROSS JOIN last_roll;

INSERT INTO fx_rule_sources (rule, kind, key)
SELECT id, 'stat', replaces FROM fx_rules WHERE id IN ('maligaro_poison_frenzy', 'maligaro_poison_power');

INSERT INTO fx_rule_when (rule, trigger) VALUES
('maligaro_poison_frenzy', 'kill'),
('maligaro_poison_power', 'kill');

INSERT INTO fx_rule_if (rule, ord, condition, negate, args) VALUES
('maligaro_poison_frenzy', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('maligaro_poison_frenzy', 1, 'enemy_poison_count_at_death', 0, '{"at_least":5}'),
('maligaro_poison_power', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('maligaro_poison_power', 1, 'enemy_poison_count_at_death', 1, '{"at_least":5}');

INSERT INTO fx_rule_do (rule, action, args) VALUES
('maligaro_poison_frenzy', 'charge', '{"charge":"frenzy"}'),
('maligaro_poison_power', 'charge', '{"charge":"power"}');
