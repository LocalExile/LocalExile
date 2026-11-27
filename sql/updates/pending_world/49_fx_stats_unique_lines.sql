-- fx_stats only. Read by code from this change on, each for one or a few unique items: "increased Damage against
-- Frozen / Ignited / Shocked / Hindered Enemies", "increased Damage with Hits and Ailments against Bleeding /
-- Chilled / Cursed Enemies" (TargetDamage.ContactDamage), and "+# Strength / Dexterity / Intelligence Requirement"
-- on an item (ItemModStats.AttributeRequirements).
UPDATE fx_stats SET handled = 'code', code = 'TargetDamage.cs', why = NULL, fit = NULL
WHERE stat IN ('damage_+%_vs_frozen_enemies', 'damage_+%_vs_ignited_enemies', 'damage_vs_shocked_enemies_+%',
  'damage_+%_vs_hindered_enemies', 'hit_and_ailment_damage_+%_vs_bleeding_enemies',
  'hit_and_ailment_damage_+%_vs_chilled_enemies', 'hit_and_ailment_damage_+%_vs_cursed_enemies');
UPDATE fx_stats SET handled = 'item', ref = 'the item''s own requirement (ItemModStats.AttributeRequirements)', why = NULL, fit = NULL
WHERE stat IN ('local_strength_requirement_+', 'local_dexterity_requirement_+', 'local_intelligence_requirement_+');

-- fx_per_attribute: a stat that gives another stat once for every so many points of an attribute ("12% increased
-- Lightning Damage per 10 Intelligence"). The code has 22 such pairs (AttributeOffenceSources); these rows add to
-- them while the effect rules are on, with complete groups of the final attribute, before pools and skills read.
-- Only for stats that reach the character: a stat that belongs to its item (Stats IsLocal) is not summed there.
CREATE TABLE fx_per_attribute (
    stat TEXT NOT NULL,
    ord INTEGER NOT NULL DEFAULT 0,
    counts_as TEXT NOT NULL,
    attribute TEXT NOT NULL CHECK (attribute IN ('strength', 'dexterity', 'intelligence')),
    every INTEGER NOT NULL CHECK (every > 0),
    scale REAL NOT NULL DEFAULT 1,
    enabled INTEGER NOT NULL DEFAULT 1,
    checked INTEGER NOT NULL DEFAULT 0,
    note TEXT,
    PRIMARY KEY (stat, ord)
) STRICT;
INSERT INTO fx_per_attribute (stat, counts_as, attribute, every) VALUES
('accuracy_rating_+%_per_25_intelligence', 'accuracy_rating_+%', 'intelligence', 25),
('area_damage_+%_per_12_strength', 'area_damage_+%', 'strength', 12),
('cast_speed_+%_per_20_dexterity', 'base_cast_speed_+%', 'dexterity', 20),
('chance_to_deal_double_damage_%_per_500_strength', 'chance_to_deal_double_damage_%', 'strength', 500),
('cold_damage_+%_per_25_dexterity', 'cold_damage_+%', 'dexterity', 25),
('cold_damage_+%_per_25_intelligence', 'cold_damage_+%', 'intelligence', 25),
('cold_damage_+%_per_25_strength', 'cold_damage_+%', 'strength', 25),
('critical_strike_chance_+%_per_8_strength', 'critical_strike_chance_+%', 'strength', 8),
('critical_strike_multiplier_+_per_25_dexterity', 'base_critical_strike_multiplier_+', 'dexterity', 25),
('damage_taken_per_250_dexterity_+%', 'base_damage_taken_+%', 'dexterity', 250),
('damage_taken_per_250_intelligence_+%', 'base_damage_taken_+%', 'intelligence', 250),
('damage_taken_per_250_strength_+%', 'base_damage_taken_+%', 'strength', 250),
('elemental_damage_+%_per_12_int', 'elemental_damage_+%', 'intelligence', 12),
('elemental_damage_+%_per_12_strength', 'elemental_damage_+%', 'strength', 12),
('fortify_duration_+%_per_10_strength', 'fortify_duration_+%', 'strength', 10),
('melee_damage_+%_per_20_intelligence', 'melee_damage_+%', 'intelligence', 20),
('melee_physical_damage_+%_per_10_dexterity', 'melee_physical_damage_+%', 'dexterity', 10),
('projectile_speed_+%_per_20_strength', 'base_projectile_speed_+%', 'strength', 20),
('stun_duration_+%_per_15_strength', 'base_stun_duration_+%', 'strength', 15),
('maximum_mana_+_per_2_intelligence', 'base_maximum_mana', 'intelligence', 2),
('accuracy_rating_+_per_2_dexterity', 'accuracy_rating', 'dexterity', 2),
('maximum_energy_shield_+_per_5_strength', 'base_maximum_energy_shield', 'strength', 5),
('spell_block_chance_%_per_50_strength', 'base_spell_block_%', 'strength', 50);
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_per_attribute', why = NULL, fit = NULL
WHERE stat IN ('accuracy_rating_+%_per_25_intelligence', 'area_damage_+%_per_12_strength', 'cast_speed_+%_per_20_dexterity', 'chance_to_deal_double_damage_%_per_500_strength', 'cold_damage_+%_per_25_dexterity', 'cold_damage_+%_per_25_intelligence', 'cold_damage_+%_per_25_strength', 'critical_strike_chance_+%_per_8_strength', 'critical_strike_multiplier_+_per_25_dexterity', 'damage_taken_per_250_dexterity_+%', 'damage_taken_per_250_intelligence_+%', 'damage_taken_per_250_strength_+%', 'elemental_damage_+%_per_12_int', 'elemental_damage_+%_per_12_strength', 'fortify_duration_+%_per_10_strength', 'melee_damage_+%_per_20_intelligence', 'melee_physical_damage_+%_per_10_dexterity', 'projectile_speed_+%_per_20_strength', 'stun_duration_+%_per_15_strength', 'maximum_mana_+_per_2_intelligence', 'accuracy_rating_+_per_2_dexterity', 'maximum_energy_shield_+_per_5_strength', 'spell_block_chance_%_per_50_strength');
