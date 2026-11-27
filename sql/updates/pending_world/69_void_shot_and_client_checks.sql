-- Void Shot (Voidfletcher), Ylfeban's random hex, the Harbinger lines' owner, and what was played in the client.

-- "Consumes a Void Charge to Trigger Level # Void Shot when you fire Arrows with a Non-Triggered Skill". The charge
-- is counted and spent in code (VoidCharges.cs, asked by the "skill" action); the shot is one attack hit in an area.
INSERT INTO fx_rules (id, book, title, replaces, status, origin, note) VALUES
('trigger_on_skill_use_%_if_you_have_a_void_arrow', 'items', 'Trigger this skill when you fire Arrows, for a Void Charge', NULL, 'built', 'parts', 'Voidfletcher. The Void Charge is taken by the skill action itself.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES ('trigger_on_skill_use_%_if_you_have_a_void_arrow', 0, 'skill', 'trigger_on_skill_use_%_if_you_have_a_void_arrow');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES ('trigger_on_skill_use_%_if_you_have_a_void_arrow', 0, 'skill_use', '{}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('trigger_on_skill_use_%_if_you_have_a_void_arrow', 0, 0, 'skill_type', 0, '{"is":"bow"}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('trigger_on_skill_use_%_if_you_have_a_void_arrow', 0, 'skill', '{"skill":"granted"}', 'enemy', '{}', '{}', '[]');
INSERT INTO effect_skills (effect, how, art, radius, note) VALUES
('VoidShot', 'area', NULL, 12, 'Void Shot (Voidfletcher): one attack hit around the aim instead of a projectile that bursts. No art; radius a guess');
UPDATE fx_stats SET handled = 'rule', ref = 'trigger_on_skill_use_%_if_you_have_a_void_arrow', why = NULL, fit = 'approximate'
WHERE stat = 'local_display_trigger_level_x_void_shot_on_arrow_fire_while_you_have_void_arrow';
UPDATE fx_stats SET handled = 'code', ref = 'VoidCharges.cs', why = NULL, fit = NULL
WHERE stat IN ('maximum_void_arrows', 'grant_void_arrow_every_x_ms');

-- "#% chance to Curse Enemies which Hit you with a random Hex, ignoring Curse Limit" (Ylfeban's Trickery): one of
-- the six hexes items apply on hit, at level 1; the curse limit still counts.
UPDATE fx_stats SET handled = 'code', ref = 'Monsters.cs', why = NULL, fit = 'approximate'
WHERE stat = 'random_curse_when_hit_%_ignoring_curse_limit';

-- The Harbinger uniques are the game's own item Harbingers now (ItemHarbingers.cs); the wearer-only buffs are gone.
UPDATE fx_stats SET ref = 'ItemHarbingers.cs' WHERE ref LIKE '%Harbingers.cs%' AND ref NOT LIKE '%ItemHarbingers.cs%';

-- Played in the client by the owner on 2026-11-25 and working.
UPDATE fx_stats SET checked = 1 WHERE stat IN (
  'melee_physical_damage_taken_%_to_deal_to_attacker', 'minimum_physical_damage_to_return_on_block',
  'maximum_physical_damage_to_return_on_block', 'minimum_lightning_damage_to_return_on_block',
  'maximum_lightning_damage_to_return_on_block', 'movement_velocity_+1%_per_X_evasion_rating', 'damage_+%_per_shock',
  'movement_velocity_+%_per_shock', 'chaos_damage_resistance_%_when_stationary', 'damage_+%_on_consecrated_ground',
  'always_ignite_while_burning', 'maximum_endurance_charges_+_while_affected_by_determination',
  'maximum_power_charges_+_while_affected_by_discipline', 'maximum_frenzy_charges_+_while_affected_by_grace',
  'lightning_damage_+%_per_10_intelligence', 'mana_gained_on_attack_hit_vs_cursed_enemies',
  'life_gained_on_attack_hit_vs_cursed_enemies', 'melee_physical_damage_+%_vs_ignited_enemies',
  'weapon_physical_damage_%_to_add_as_random_element');
UPDATE fx_modifiers SET checked = 1 WHERE stat IN ('damage_+%_per_shock', 'movement_velocity_+%_per_shock',
  'chaos_damage_resistance_%_when_stationary', 'damage_+%_on_consecrated_ground', 'always_ignite_while_burning',
  'maximum_endurance_charges_+_while_affected_by_determination', 'maximum_power_charges_+_while_affected_by_discipline',
  'maximum_frenzy_charges_+_while_affected_by_grace');
UPDATE fx_per_attribute SET checked = 1 WHERE stat = 'lightning_damage_+%_per_10_intelligence';
UPDATE fx_rules SET checked = 1 WHERE id IN ('mana_on_attack_hit_vs_cursed', 'life_on_attack_hit_vs_cursed', 'onslaught_on_vaal_skill_use');
UPDATE fx_aliases SET checked = 1 WHERE stat = 'weapon_physical_damage_%_to_add_as_random_element';
UPDATE fx_item_local SET checked = 1 WHERE note LIKE '%Shavronne%' OR stat IN (SELECT stat FROM fx_item_local WHERE note LIKE '%Shavronne%');
UPDATE effect_skills SET checked = 1 WHERE effect IN ('TriggeredConsecrate', 'TriggeredShockedGround');
