-- Damage returned to attackers, and a batch of unique lines done with rows.

-- "Reflects # Damage to Melee Attackers", "Reflects # Damage to Attackers on Block" and "#% of Melee Physical Damage
-- taken reflected to Attacker" (Bramblejack, Wall of Brambles, The Anvil, The Blood Thorn, Veil of the Night, ...).
UPDATE fx_stats SET handled = 'code', ref = 'ReflectToAttackers.cs', why = NULL, fit = NULL
WHERE stat LIKE '%damage_to_return_to_melee_attacker' OR stat LIKE '%damage_to_return_on_block'
   OR stat IN ('melee_physical_damage_taken_%_to_deal_to_attacker', 'map_melee_physical_damage_taken_%_to_deal_to_attacker');

-- Queen of the Forest's movement speed from evasion; Razor of the Seventh Sun's melee physical damage against ignited.
UPDATE fx_stats SET handled = 'code', ref = 'Progression.cs', why = NULL, fit = 'approximate'
WHERE stat = 'movement_velocity_+1%_per_X_evasion_rating';
UPDATE fx_stats SET handled = 'code', ref = 'TargetDamage.cs', why = NULL, fit = NULL
WHERE stat = 'melee_physical_damage_+%_vs_ignited_enemies';

-- Lines that are a plain stat under a condition the tables already know.
INSERT INTO fx_modifiers (stat, ord, quantity, how, scale, conditions, target, note, counts_as) VALUES
('damage_+%_per_shock', 0, 'damage', 'increased', 1, '[{"if":"state","is":"while_shocked"}]', 'self', '{0}% increased Damage while Shocked (Maligaro''s Restraint)', 'damage_+%'),
('movement_velocity_+%_per_shock', 0, 'base_movement_velocity', 'increased', 1, '[{"if":"state","is":"while_shocked"}]', 'self', '{0}% increased Movement Speed while Shocked (Maligaro''s Restraint)', 'base_movement_velocity_+%'),
('chaos_damage_resistance_%_when_stationary', 0, 'chaos_damage_resistance_%', 'flat', 1, '[{"if":"state","is":"while_stationary"}]', 'self', '+{0}% to Chaos Resistance while stationary (Replica Red Trail)', 'base_chaos_damage_resistance_%'),
('damage_+%_on_consecrated_ground', 0, 'damage', 'increased', 1, '[{"if":"state","is":"while_on_consecrated_ground"}]', 'self', '{0}% increased Damage while on Consecrated Ground (Gifts from Above)', 'damage_+%'),
('always_ignite_while_burning', 0, 'always_ignite', 'flag', 1, '[{"if":"state","is":"while_ignited"}]', 'self', 'You always Ignite while Burning (Glitterdisc): while ignited, not for other burning', 'always_ignite'),
('maximum_endurance_charges_+_while_affected_by_determination', 0, 'max_endurance_charges', 'flat', 1, '[{"if":"affected_by","aura":"determination"}]', 'self', 'Sublime Vision', 'max_endurance_charges'),
('maximum_power_charges_+_while_affected_by_discipline', 0, 'max_power_charges', 'flat', 1, '[{"if":"affected_by","aura":"discipline"}]', 'self', 'Sublime Vision', 'max_power_charges'),
('maximum_frenzy_charges_+_while_affected_by_grace', 0, 'max_frenzy_charges', 'flat', 1, '[{"if":"affected_by","aura":"grace"}]', 'self', 'Sublime Vision', 'max_frenzy_charges');
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_modifiers', why = NULL, fit = NULL
WHERE stat IN ('damage_+%_per_shock', 'movement_velocity_+%_per_shock', 'chaos_damage_resistance_%_when_stationary',
  'damage_+%_on_consecrated_ground', 'maximum_endurance_charges_+_while_affected_by_determination',
  'maximum_power_charges_+_while_affected_by_discipline', 'maximum_frenzy_charges_+_while_affected_by_grace');
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_modifiers', why = NULL, fit = 'approximate' WHERE stat = 'always_ignite_while_burning';

-- "#% increased Lightning Damage per 10 Intelligence" (Saemus' Gift).
INSERT INTO fx_per_attribute (stat, ord, counts_as, attribute, every, scale) VALUES
('lightning_damage_+%_per_10_intelligence', 0, 'lightning_damage_+%', 'intelligence', 10, 1);
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_per_attribute', why = NULL, fit = NULL WHERE stat = 'lightning_damage_+%_per_10_intelligence';

-- "Gain # Mana per Cursed Enemy Hit with Attacks" (Gluttony).
INSERT INTO fx_rules (id, book, title, replaces, status, origin, note) VALUES
('mana_on_attack_hit_vs_cursed', 'items', 'Gain Mana per Cursed Enemy Hit with Attacks', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before. Gluttony.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES ('mana_on_attack_hit_vs_cursed', 0, 'stat', 'mana_gained_on_attack_hit_vs_cursed_enemies');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES ('mana_on_attack_hit_vs_cursed', 0, 'hit', '{}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('mana_on_attack_hit_vs_cursed', 0, 0, 'skill_type', 0, '{"is":"attack"}'),
('mana_on_attack_hit_vs_cursed', 0, 1, 'enemy_state', 0, '{"is":"cursed"}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('mana_on_attack_hit_vs_cursed', 0, 'recover', '{"pool":"mana"}', 'self', '{}', '{"from":"roll"}', '[]');
UPDATE fx_stats SET handled = 'rule', ref = 'mana_on_attack_hit_vs_cursed', why = NULL, fit = NULL WHERE stat = 'mana_gained_on_attack_hit_vs_cursed_enemies';
