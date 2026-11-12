-- The ten drop-disabled Loyalty rows of issue #372: each replaces a small attribute passive with a level 1
-- "Trigger level 1 Summon Spirit of X Skill when ..." granted skill. The row is the replacement passive's canonical
-- stat set from the client's PassiveSkillOverrides: its has_trigger_on_* stat, which the Spirit chieftain machinery
-- (SpiritChieftains.Rules) reads; the granted skill itself is PassiveSkillOverrides.Effect (TattooCatalogue.Entry).
-- Consumers: the ten trigger hooks (suppress/spell hit from a Unique, maximum Rage, travel Skill, critical strike
-- from a Unique, totem death, block, low Life, critical strike against a marked Unique, Energy Shield Recharge,
-- Savage Hit) summon the spirit at level 1 for its duration with its Leadership buff and fighting chieftain.

INSERT INTO tattoo_effects (name, enabled) VALUES
('Loyalty Tattoo of Ahuana', 1),
('Loyalty Tattoo of Akoya', 1),
('Loyalty Tattoo of Ikiaho', 1),
('Loyalty Tattoo of Kahuturoa', 1),
('Loyalty Tattoo of Kaom', 1),
('Loyalty Tattoo of Kiloava', 1),
('Loyalty Tattoo of Maata', 1),
('Loyalty Tattoo of Rakiata', 1),
('Loyalty Tattoo of Tawhanuku', 1),
('Loyalty Tattoo of Utula', 1);

INSERT INTO tattoo_effect_stats (tattoo, ord, stat, value) VALUES
('Loyalty Tattoo of Ahuana', 0, 'has_trigger_on_suppress_hit_from_unique', 1),
('Loyalty Tattoo of Akoya', 0, 'has_trigger_on_reaching_maximum_rage_in_presence_of_unique', 1),
('Loyalty Tattoo of Ikiaho', 0, 'has_trigger_on_travel_skill_use_in_presence_of_unique', 1),
('Loyalty Tattoo of Kahuturoa', 0, 'has_trigger_on_crit_by_unique_enemy', 1),
('Loyalty Tattoo of Kaom', 0, 'has_trigger_on_totem_death_in_presence_of_unique', 1),
('Loyalty Tattoo of Kiloava', 0, 'has_trigger_on_block_hit_from_unique', 1),
('Loyalty Tattoo of Maata', 0, 'has_trigger_on_reaching_low_life_in_presence_of_unique', 1),
('Loyalty Tattoo of Rakiata', 0, 'has_trigger_on_crit_vs_marked_unique', 1),
('Loyalty Tattoo of Tawhanuku', 0, 'has_trigger_on_es_recharge_in_presence_of_unique', 1),
('Loyalty Tattoo of Utula', 0, 'has_trigger_on_taking_savage_hit_from_unique', 1);
