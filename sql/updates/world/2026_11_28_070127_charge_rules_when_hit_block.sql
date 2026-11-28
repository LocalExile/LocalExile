-- Every charge rule when hit and on block, in their old order.

INSERT INTO fx_rules (id, book, title, replaces, status, position) VALUES
('chance_to_gain_endurance_charge_when_hit_%', 'charges', 'Chance to gain an Endurance Charge when Hit', 'chance_to_gain_endurance_charge_when_hit_%', 'built', 240),
('add_frenzy_charge_when_hit_%', 'charges', 'Chance to gain a Frenzy Charge when Hit', 'add_frenzy_charge_when_hit_%', 'built', 250),
('chance_to_gain_endurance_charge_on_block_%', 'charges', 'Chance to gain an Endurance Charge when you Block', 'chance_to_gain_endurance_charge_on_block_%', 'built', 270),
('chance_to_gain_frenzy_charge_on_block_%', 'charges', 'Chance to gain a Frenzy Charge when you Block', 'chance_to_gain_frenzy_charge_on_block_%', 'built', 280),
('chance_to_gain_frenzy_charge_on_block_attack_%', 'charges', 'Chance to gain a Frenzy Charge when you Block an Attack', 'chance_to_gain_frenzy_charge_on_block_attack_%', 'built', 290),
('power_charge_on_block_%_chance', 'charges', 'Chance to gain a Power Charge when you Block', 'power_charge_on_block_%_chance', 'built', 300),
('chance_to_gain_power_charge_when_block_%', 'charges', 'Chance to gain a Power Charge when you Block', 'chance_to_gain_power_charge_when_block_%', 'built', 310),
('power_charge_on_spell_block_%_chance', 'charges', 'Chance to gain a Power Charge when you Block a Spell', 'power_charge_on_spell_block_%_chance', 'built', 320),
('gain_random_charge_on_block', 'charges', 'Gain a random Charge when you Block', 'gain_random_charge_on_block', 'built', 330),
('gain_maximum_endurance_charges_when_crit_chance_%', 'charges', 'Chance to gain maximum Endurance Charges when you take a Critical Strike', 'gain_maximum_endurance_charges_when_crit_chance_%', 'built', 510),
('unique_lose_all_endurance_charges_when_hit', 'charges', 'Lose all Endurance Charges when Hit', 'unique_lose_all_endurance_charges_when_hit', 'built', 520),
('unique_lose_a_power_charge_when_hit', 'charges', 'Lose a Power Charge when Hit', 'unique_lose_a_power_charge_when_hit', 'built', 530),
('lose_all_power_charges_on_block', 'charges', 'Lose all Power Charges when you Block', 'lose_all_power_charges_on_block', 'built', 540);

INSERT INTO fx_rule_sources (rule, kind, key)
SELECT id, 'stat', replaces FROM fx_rules WHERE id IN ('chance_to_gain_endurance_charge_when_hit_%', 'add_frenzy_charge_when_hit_%', 'chance_to_gain_endurance_charge_on_block_%', 'chance_to_gain_frenzy_charge_on_block_%', 'chance_to_gain_frenzy_charge_on_block_attack_%', 'power_charge_on_block_%_chance', 'chance_to_gain_power_charge_when_block_%', 'power_charge_on_spell_block_%_chance', 'gain_random_charge_on_block', 'gain_maximum_endurance_charges_when_crit_chance_%', 'unique_lose_all_endurance_charges_when_hit', 'unique_lose_a_power_charge_when_hit', 'lose_all_power_charges_on_block');

INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('chance_to_gain_endurance_charge_when_hit_%', 0, 'when_hit', '{}'),
('chance_to_gain_endurance_charge_when_hit_%', 1, 'when_hit', '{"who":"monster"}'),
('add_frenzy_charge_when_hit_%', 0, 'when_hit', '{}'),
('chance_to_gain_endurance_charge_on_block_%', 0, 'block', '{}'),
('chance_to_gain_frenzy_charge_on_block_%', 0, 'block', '{}'),
('chance_to_gain_frenzy_charge_on_block_attack_%', 0, 'block', '{}'),
('power_charge_on_block_%_chance', 0, 'block', '{}'),
('chance_to_gain_power_charge_when_block_%', 0, 'block', '{}'),
('power_charge_on_spell_block_%_chance', 0, 'block', '{}'),
('gain_random_charge_on_block', 0, 'block', '{}'),
('gain_maximum_endurance_charges_when_crit_chance_%', 0, 'when_hit', '{}'),
('unique_lose_all_endurance_charges_when_hit', 0, 'when_hit', '{}'),
('unique_lose_a_power_charge_when_hit', 0, 'when_hit', '{}'),
('lose_all_power_charges_on_block', 0, 'block', '{}');

INSERT INTO fx_rule_if (rule, ord, condition, negate, args) VALUES
('chance_to_gain_endurance_charge_when_hit_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('add_frenzy_charge_when_hit_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chance_to_gain_endurance_charge_on_block_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chance_to_gain_frenzy_charge_on_block_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chance_to_gain_frenzy_charge_on_block_attack_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chance_to_gain_frenzy_charge_on_block_attack_%', 1, 'skill_type', 0, '{"is":"attack"}'),
('power_charge_on_block_%_chance', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chance_to_gain_power_charge_when_block_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('power_charge_on_spell_block_%_chance', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('power_charge_on_spell_block_%_chance', 1, 'skill_type', 0, '{"is":"spell"}'),
('gain_maximum_endurance_charges_when_crit_chance_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('gain_maximum_endurance_charges_when_crit_chance_%', 1, 'critical', 0, '{}');

INSERT INTO fx_rule_do (rule, ord, action, args) VALUES
('chance_to_gain_endurance_charge_when_hit_%', 0, 'charge', '{"charge":"endurance"}'),
('add_frenzy_charge_when_hit_%', 0, 'charge', '{"charge":"frenzy"}'),
('chance_to_gain_endurance_charge_on_block_%', 0, 'charge', '{"charge":"endurance"}'),
('chance_to_gain_frenzy_charge_on_block_%', 0, 'charge', '{"charge":"frenzy"}'),
('chance_to_gain_frenzy_charge_on_block_attack_%', 0, 'charge', '{"charge":"frenzy"}'),
('power_charge_on_block_%_chance', 0, 'charge', '{"charge":"power"}'),
('chance_to_gain_power_charge_when_block_%', 0, 'charge', '{"charge":"power"}'),
('power_charge_on_spell_block_%_chance', 0, 'charge', '{"charge":"power"}'),
('gain_random_charge_on_block', 0, 'charge', '{"charge":"random"}'),
('gain_maximum_endurance_charges_when_crit_chance_%', 0, 'charge', '{"charge":"endurance","does":"to_maximum"}'),
('unique_lose_all_endurance_charges_when_hit', 0, 'charge', '{"charge":"endurance","does":"lose_all"}'),
('unique_lose_a_power_charge_when_hit', 0, 'charge', '{"charge":"power","does":"lose_one"}'),
('lose_all_power_charges_on_block', 0, 'charge', '{"charge":"power","does":"lose_all"}');
