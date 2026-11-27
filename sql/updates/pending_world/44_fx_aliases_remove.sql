-- Aliases from the table, the part "remove", and a correction of fx_stats.
--
-- fx_aliases: a stat that is also another stat, with no condition ("increased Global Damage" on a weapon is the
-- character's increased damage). The code has seven such pairs (ItemStatAliases); these rows add to them while the
-- effect rules are on. scale turns one unit into the other (a percent into hundredths of a percent is 100).
CREATE TABLE fx_aliases (
    stat TEXT NOT NULL,
    ord INTEGER NOT NULL DEFAULT 0,
    counts_as TEXT NOT NULL,
    scale REAL NOT NULL DEFAULT 1,
    enabled INTEGER NOT NULL DEFAULT 1,
    checked INTEGER NOT NULL DEFAULT 0,
    note TEXT,
    PRIMARY KEY (stat, ord)
) STRICT;
INSERT INTO fx_aliases (stat, ord, counts_as, scale, note) VALUES
('on_weapon_global_damage_+%', 0, 'damage_+%', 1, '{0}% increased Global Damage'),
('base_physical_damage_reduction_and_evasion_rating', 0, 'base_physical_damage_reduction_rating', 1, '{0:+d} to Armour and Evasion Rating'),
('base_physical_damage_reduction_and_evasion_rating', 1, 'base_evasion_rating', 1, NULL),
('damage_+%_with_bow_skills', 0, 'bow_damage_+%', 1, '{}% increased Damage with Bow Skills'),
('old_do_not_use_life_leech_from_physical_damage_%', 0, 'life_leech_from_physical_attack_damage_permyriad', 100, '{0}% of Physical Attack Damage Leeched as Life: a percent, the plain stat counts in hundredths'),
('old_do_not_use_mana_leech_from_physical_damage_%', 0, 'mana_leech_from_physical_attack_damage_permyriad', 100, '{0}% of Physical Attack Damage Leeched as Mana: a percent, the plain stat counts in hundredths'),
('old_do_not_use_spell_block_%_from_assumed_block_value', 0, 'base_spell_block_%', 1, '{0}% Chance to Block Spell Damage'),
('old_do_not_use_base_life_leech_from_fire_damage_permyriad', 0, 'base_life_leech_from_fire_damage_permyriad', 1, NULL),
('old_do_not_use_base_life_leech_from_cold_damage_permyriad', 0, 'base_life_leech_from_cold_damage_permyriad', 1, NULL),
('old_do_not_use_base_life_leech_from_lightning_damage_permyriad', 0, 'base_life_leech_from_lightning_damage_permyriad', 1, NULL);
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_aliases: counts as damage_+%', why = NULL, fit = NULL WHERE stat = 'on_weapon_global_damage_+%';
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_aliases: counts as base_physical_damage_reduction_rating and base_evasion_rating', why = NULL, fit = NULL WHERE stat = 'base_physical_damage_reduction_and_evasion_rating';
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_aliases: counts as bow_damage_+%', why = NULL, fit = NULL WHERE stat = 'damage_+%_with_bow_skills';
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_aliases: counts as life_leech_from_physical_attack_damage_permyriad', why = NULL, fit = NULL WHERE stat = 'old_do_not_use_life_leech_from_physical_damage_%';
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_aliases: counts as mana_leech_from_physical_attack_damage_permyriad', why = NULL, fit = NULL WHERE stat = 'old_do_not_use_mana_leech_from_physical_damage_%';
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_aliases: counts as base_spell_block_%', why = NULL, fit = NULL WHERE stat = 'old_do_not_use_spell_block_%_from_assumed_block_value';
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_aliases: counts as base_life_leech_from_fire_damage_permyriad', why = NULL, fit = NULL WHERE stat = 'old_do_not_use_base_life_leech_from_fire_damage_permyriad';
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_aliases: counts as base_life_leech_from_cold_damage_permyriad', why = NULL, fit = NULL WHERE stat = 'old_do_not_use_base_life_leech_from_cold_damage_permyriad';
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_aliases: counts as base_life_leech_from_lightning_damage_permyriad', why = NULL, fit = NULL WHERE stat = 'old_do_not_use_base_life_leech_from_lightning_damage_permyriad';
-- The part "remove": an ailment taken off the actor at once, with the removal a flask's own suffix already does.
UPDATE fx_actions SET built = 1, origin = 'parts',
  takes = 'what: bleeding, poison, chill freeze, ignite (and burning), shock, corrupted blood, elemental (chill, freeze, ignite, shock) or ailments (all); value: seconds it then cannot be put on again (none unless given)',
  note = 'Takes the ailment off the actor at once. Curses and other debuffs are not removed yet.'
WHERE id = 'remove';
INSERT INTO fx_rules (id, book, title, replaces, status, origin, note) VALUES
('remove_chill_and_freeze_on_flask_use', 'recovery', 'Remove Chill and Freeze when you use a Flask', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('remove_ignite_and_burning_on_flask_use', 'recovery', 'Remove Ignite and Burning when you use a Flask', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('flasks_dispel_burning', 'recovery', 'Removes Burning when you use a Flask', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('remove_shock_on_flask_use', 'recovery', 'Remove Shock when you use a Flask', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('remove_bleed_on_flask_use', 'recovery', 'Removes Bleeding when you use a Flask', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('remove_bleed_on_life_flask_use', 'recovery', 'Remove Bleeding when you use a Life Flask', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('dispel_status_ailments_on_flask_use', 'recovery', 'Removes Elemental Ailments when you use a Flask', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('dispel_bleed_on_guard_skill_use', 'recovery', 'Remove Bleeding when you use a Guard Skill', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('dispel_corrupted_blood_on_guard_skill_use', 'recovery', 'Remove Corrupted Blood when you use a Guard Skill', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('remove_elemental_ailments_on_curse_cast_%', 'recovery', '#% chance to remove Elemental Ailments when you Cast a Curse Spell', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('remove_chill_and_freeze_on_flask_use', 0, 'stat', 'remove_chill_and_freeze_on_flask_use'),
('remove_ignite_and_burning_on_flask_use', 0, 'stat', 'remove_ignite_and_burning_on_flask_use'),
('flasks_dispel_burning', 0, 'stat', 'flasks_dispel_burning'),
('remove_shock_on_flask_use', 0, 'stat', 'remove_shock_on_flask_use'),
('remove_bleed_on_flask_use', 0, 'stat', 'remove_bleed_on_flask_use'),
('remove_bleed_on_life_flask_use', 0, 'stat', 'remove_bleed_on_life_flask_use'),
('dispel_status_ailments_on_flask_use', 0, 'stat', 'dispel_status_ailments_on_flask_use'),
('dispel_bleed_on_guard_skill_use', 0, 'stat', 'dispel_bleed_on_guard_skill_use'),
('dispel_corrupted_blood_on_guard_skill_use', 0, 'stat', 'dispel_corrupted_blood_on_guard_skill_use'),
('remove_elemental_ailments_on_curse_cast_%', 0, 'stat', 'remove_elemental_ailments_on_curse_cast_%');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('remove_chill_and_freeze_on_flask_use', 0, 'flask_use', '{}'),
('remove_ignite_and_burning_on_flask_use', 0, 'flask_use', '{}'),
('flasks_dispel_burning', 0, 'flask_use', '{}'),
('remove_shock_on_flask_use', 0, 'flask_use', '{}'),
('remove_bleed_on_flask_use', 0, 'flask_use', '{}'),
('remove_bleed_on_life_flask_use', 0, 'flask_use', '{}'),
('dispel_status_ailments_on_flask_use', 0, 'flask_use', '{}'),
('dispel_bleed_on_guard_skill_use', 0, 'skill_use', '{}'),
('dispel_corrupted_blood_on_guard_skill_use', 0, 'skill_use', '{}'),
('remove_elemental_ailments_on_curse_cast_%', 0, 'skill_use', '{}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('remove_bleed_on_life_flask_use', 0, 0, 'flask_kind', 0, '{"is":"life"}'),
('dispel_bleed_on_guard_skill_use', 0, 0, 'skill_type', 0, '{"is":"guard"}'),
('dispel_corrupted_blood_on_guard_skill_use', 0, 0, 'skill_type', 0, '{"is":"guard"}'),
('remove_elemental_ailments_on_curse_cast_%', 0, 0, 'skill_type', 0, '{"is":"curse"}'),
('remove_elemental_ailments_on_curse_cast_%', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('remove_chill_and_freeze_on_flask_use', 0, 'remove', '{"what":"chill_freeze"}', 'self', '{}', '{}', '[]'),
('remove_ignite_and_burning_on_flask_use', 0, 'remove', '{"what":"ignite"}', 'self', '{}', '{}', '[]'),
('flasks_dispel_burning', 0, 'remove', '{"what":"ignite"}', 'self', '{}', '{}', '[]'),
('remove_shock_on_flask_use', 0, 'remove', '{"what":"shock"}', 'self', '{}', '{}', '[]'),
('remove_bleed_on_flask_use', 0, 'remove', '{"what":"bleeding"}', 'self', '{}', '{}', '[]'),
('remove_bleed_on_life_flask_use', 0, 'remove', '{"what":"bleeding"}', 'self', '{}', '{}', '[]'),
('dispel_status_ailments_on_flask_use', 0, 'remove', '{"what":"elemental"}', 'self', '{}', '{}', '[]'),
('dispel_bleed_on_guard_skill_use', 0, 'remove', '{"what":"bleeding"}', 'self', '{}', '{}', '[]'),
('dispel_corrupted_blood_on_guard_skill_use', 0, 'remove', '{"what":"corrupted blood"}', 'self', '{}', '{}', '[]'),
('remove_elemental_ailments_on_curse_cast_%', 0, 'remove', '{"what":"elemental"}', 'self', '{}', '{}', '[]');
UPDATE fx_stats SET handled = 'rule', ref = 'remove_chill_and_freeze_on_flask_use', why = NULL, fit = NULL WHERE stat = 'remove_chill_and_freeze_on_flask_use';
UPDATE fx_stats SET handled = 'rule', ref = 'remove_ignite_and_burning_on_flask_use', why = NULL, fit = NULL WHERE stat = 'remove_ignite_and_burning_on_flask_use';
UPDATE fx_stats SET handled = 'rule', ref = 'flasks_dispel_burning', why = NULL, fit = NULL WHERE stat = 'flasks_dispel_burning';
UPDATE fx_stats SET handled = 'rule', ref = 'remove_shock_on_flask_use', why = NULL, fit = NULL WHERE stat = 'remove_shock_on_flask_use';
UPDATE fx_stats SET handled = 'rule', ref = 'remove_bleed_on_flask_use', why = NULL, fit = NULL WHERE stat = 'remove_bleed_on_flask_use';
UPDATE fx_stats SET handled = 'rule', ref = 'remove_bleed_on_life_flask_use', why = NULL, fit = NULL WHERE stat = 'remove_bleed_on_life_flask_use';
UPDATE fx_stats SET handled = 'rule', ref = 'dispel_status_ailments_on_flask_use', why = NULL, fit = NULL WHERE stat = 'dispel_status_ailments_on_flask_use';
UPDATE fx_stats SET handled = 'rule', ref = 'dispel_bleed_on_guard_skill_use', why = NULL, fit = NULL WHERE stat = 'dispel_bleed_on_guard_skill_use';
UPDATE fx_stats SET handled = 'rule', ref = 'dispel_corrupted_blood_on_guard_skill_use', why = NULL, fit = NULL WHERE stat = 'dispel_corrupted_blood_on_guard_skill_use';
UPDATE fx_stats SET handled = 'rule', ref = 'remove_elemental_ailments_on_curse_cast_%', why = NULL, fit = NULL WHERE stat = 'remove_elemental_ailments_on_curse_cast_%';
-- Read by code from this change on: "Adds # to # ... Damage with Axe, Mace or Sceptre, Staff and Sword Attacks"
-- (SupportedAttackDamage), Overwhelm (the hit's physical mitigation), and "Minions take #% increased Damage".
UPDATE fx_stats SET handled = 'code', code = 'SupportedAttackDamage.cs', why = NULL, fit = NULL WHERE stat IN ('attack_maximum_added_cold_damage_with_axes', 'attack_maximum_added_fire_damage_with_axes', 'attack_maximum_added_lightning_damage_with_axes', 'attack_maximum_added_physical_damage_with_axes', 'attack_minimum_added_cold_damage_with_axes', 'attack_minimum_added_fire_damage_with_axes', 'attack_minimum_added_lightning_damage_with_axes', 'attack_minimum_added_physical_damage_with_axes', 'attack_maximum_added_cold_damage_with_maces', 'attack_maximum_added_cold_damage_with_staves', 'attack_maximum_added_cold_damage_with_swords', 'attack_maximum_added_fire_damage_with_maces', 'attack_maximum_added_fire_damage_with_staves', 'attack_maximum_added_fire_damage_with_swords', 'attack_maximum_added_lightning_damage_with_maces', 'attack_maximum_added_lightning_damage_with_staves', 'attack_maximum_added_lightning_damage_with_swords', 'attack_maximum_added_physical_damage_with_maces', 'attack_maximum_added_physical_damage_with_staves', 'attack_maximum_added_physical_damage_with_swords', 'attack_minimum_added_cold_damage_with_maces', 'attack_minimum_added_cold_damage_with_staves', 'attack_minimum_added_cold_damage_with_swords', 'attack_minimum_added_fire_damage_with_maces', 'attack_minimum_added_fire_damage_with_staves', 'attack_minimum_added_fire_damage_with_swords', 'attack_minimum_added_lightning_damage_with_maces', 'attack_minimum_added_lightning_damage_with_staves', 'attack_minimum_added_lightning_damage_with_swords', 'attack_minimum_added_physical_damage_with_maces', 'attack_minimum_added_physical_damage_with_staves', 'attack_minimum_added_physical_damage_with_swords');
UPDATE fx_stats SET handled = 'code', code = 'Combat.cs, MonsterMods.cs', why = NULL, fit = NULL WHERE stat = 'enemy_phys_reduction_%_penalty_vs_hit';
UPDATE fx_stats SET handled = 'code', code = 'GolemSkills.cs', why = NULL, fit = NULL WHERE stat = 'minion_damage_taken_+%';
-- Read by code already, shown as unbuilt until now: melee strike range (Combat.cs), seven minion stats that minions
-- carry under their plain name and monster code reads there, and Alchemist's Genius (main, 2026-11-24).
UPDATE fx_stats SET handled = 'code', why = NULL, fit = NULL WHERE stat IN ('melee_range_+', 'minion_chance_to_ignite_%', 'minion_evasion_rating_+%', 'minion_spell_suppression_chance_%', 'minion_projectile_speed_+%', 'minion_physical_damage_%_to_add_as_fire', 'minion_physical_damage_%_to_add_as_cold', 'minion_has_unholy_might', 'gain_alchemists_genius_on_flask_use_%');
