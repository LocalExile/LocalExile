-- Every charge rule on stun and on skill use, in their old order.

INSERT INTO fx_rules (id, book, title, replaces, status, position) VALUES
('chance_to_gain_endurance_charge_when_you_stun_enemy_%', 'charges', 'Chance to gain an Endurance Charge when you Stun an Enemy', 'chance_to_gain_endurance_charge_when_you_stun_enemy_%', 'built', 260),
('chance_to_gain_power_charge_on_stun_%', 'charges', 'Chance to gain a Power Charge when you Stun', 'chance_to_gain_power_charge_on_stun_%', 'built', 460),
('chance_to_gain_frenzy_charge_on_stun_%', 'charges', 'Chance to gain a Frenzy Charge when you Stun', 'chance_to_gain_frenzy_charge_on_stun_%', 'built', 470),
('chance_to_gain_power_charge_on_melee_stun_%', 'charges', 'Chance to gain a Power Charge when you Stun with Melee Damage', 'chance_to_gain_power_charge_on_melee_stun_%', 'built', 480),
('gain_endurance_charge_on_melee_stun_%', 'charges', 'Chance to gain an Endurance Charge when you Stun an Enemy with Melee Damage', 'gain_endurance_charge_on_melee_stun_%', 'built', 490),
('gain_endurance_charge_on_melee_stun', 'charges', 'Gain an Endurance Charge when you Stun an Enemy with Melee Damage', 'gain_endurance_charge_on_melee_stun', 'built', 500),
('gain_frenzy_power_endurance_charges_on_vaal_skill_use', 'charges', 'Gain a Frenzy, Power and Endurance Charge when you use a Vaal Skill', 'gain_frenzy_power_endurance_charges_on_vaal_skill_use', 'built', 550),
('gain_power_charge_on_vaal_skill_use_%', 'charges', 'Chance to gain a Power Charge when you use a Vaal Skill', 'gain_power_charge_on_vaal_skill_use_%', 'built', 580),
('gain_power_charge_on_curse_cast_%', 'charges', 'Chance to gain a Power Charge when you Cast a Curse Spell', 'gain_power_charge_on_curse_cast_%', 'built', 590),
('gain_power_charge_when_throwing_trap_%', 'charges', 'Chance to gain a Power Charge when you Throw a Trap', 'gain_power_charge_when_throwing_trap_%', 'built', 600);

INSERT INTO fx_rule_sources (rule, kind, key)
SELECT id, 'stat', replaces FROM fx_rules WHERE id IN ('chance_to_gain_endurance_charge_when_you_stun_enemy_%', 'chance_to_gain_power_charge_on_stun_%', 'chance_to_gain_frenzy_charge_on_stun_%', 'chance_to_gain_power_charge_on_melee_stun_%', 'gain_endurance_charge_on_melee_stun_%', 'gain_endurance_charge_on_melee_stun', 'gain_frenzy_power_endurance_charges_on_vaal_skill_use', 'gain_power_charge_on_vaal_skill_use_%', 'gain_power_charge_on_curse_cast_%', 'gain_power_charge_when_throwing_trap_%');

INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('chance_to_gain_endurance_charge_when_you_stun_enemy_%', 0, 'stun', '{}'),
('chance_to_gain_power_charge_on_stun_%', 0, 'stun', '{}'),
('chance_to_gain_frenzy_charge_on_stun_%', 0, 'stun', '{}'),
('chance_to_gain_power_charge_on_melee_stun_%', 0, 'stun', '{}'),
('gain_endurance_charge_on_melee_stun_%', 0, 'stun', '{}'),
('gain_endurance_charge_on_melee_stun', 0, 'stun', '{}'),
('gain_frenzy_power_endurance_charges_on_vaal_skill_use', 0, 'skill_use', '{}'),
('gain_power_charge_on_vaal_skill_use_%', 0, 'skill_use', '{}'),
('gain_power_charge_on_curse_cast_%', 0, 'skill_use', '{}'),
('gain_power_charge_when_throwing_trap_%', 0, 'skill_use', '{}');

INSERT INTO fx_rule_if (rule, ord, condition, negate, args) VALUES
('chance_to_gain_endurance_charge_when_you_stun_enemy_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chance_to_gain_power_charge_on_stun_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chance_to_gain_frenzy_charge_on_stun_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chance_to_gain_power_charge_on_melee_stun_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('chance_to_gain_power_charge_on_melee_stun_%', 1, 'skill_type', 0, '{"is":"melee"}'),
('gain_endurance_charge_on_melee_stun_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('gain_endurance_charge_on_melee_stun_%', 1, 'skill_type', 0, '{"is":"melee"}'),
('gain_endurance_charge_on_melee_stun', 0, 'skill_type', 0, '{"is":"melee"}'),
('gain_frenzy_power_endurance_charges_on_vaal_skill_use', 0, 'skill_type', 0, '{"is":"vaal"}'),
('gain_power_charge_on_vaal_skill_use_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('gain_power_charge_on_vaal_skill_use_%', 1, 'skill_type', 0, '{"is":"vaal"}'),
('gain_power_charge_on_curse_cast_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('gain_power_charge_on_curse_cast_%', 1, 'skill_type', 0, '{"is":"curse"}'),
('gain_power_charge_when_throwing_trap_%', 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('gain_power_charge_when_throwing_trap_%', 1, 'skill_type', 0, '{"is":"trap"}');

INSERT INTO fx_rule_do (rule, ord, action, args) VALUES
('chance_to_gain_endurance_charge_when_you_stun_enemy_%', 0, 'charge', '{"charge":"endurance"}'),
('chance_to_gain_power_charge_on_stun_%', 0, 'charge', '{"charge":"power"}'),
('chance_to_gain_frenzy_charge_on_stun_%', 0, 'charge', '{"charge":"frenzy"}'),
('chance_to_gain_power_charge_on_melee_stun_%', 0, 'charge', '{"charge":"power"}'),
('gain_endurance_charge_on_melee_stun_%', 0, 'charge', '{"charge":"endurance"}'),
('gain_endurance_charge_on_melee_stun', 0, 'charge', '{"charge":"endurance"}'),
('gain_frenzy_power_endurance_charges_on_vaal_skill_use', 0, 'charge', '{"charge":"endurance"}'),
('gain_frenzy_power_endurance_charges_on_vaal_skill_use', 1, 'charge', '{"charge":"frenzy"}'),
('gain_frenzy_power_endurance_charges_on_vaal_skill_use', 2, 'charge', '{"charge":"power"}'),
('gain_power_charge_on_vaal_skill_use_%', 0, 'charge', '{"charge":"power"}'),
('gain_power_charge_on_curse_cast_%', 0, 'charge', '{"charge":"power"}'),
('gain_power_charge_when_throwing_trap_%', 0, 'charge', '{"charge":"power"}');
