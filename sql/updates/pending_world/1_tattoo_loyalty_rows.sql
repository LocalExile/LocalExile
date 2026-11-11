-- The ten drop-disabled Loyalty rows of issue #372: each replaces a small attribute passive with a level 1
-- "Trigger level 1 Summon Spirit of X Skill when ..." granted skill. The row is the replacement passive's canonical
-- stat set from the client's PassiveSkillOverrides: its has_trigger_on_* stat, which the Spirit chieftain machinery
-- (SpiritChieftains.Rules) reads; the granted skill itself is PassiveSkillOverrides.Effect (TattooCatalogue.Entry).
-- Consumers: the ten trigger hooks (suppress/spell hit from a Unique, maximum Rage, travel Skill, critical strike
-- from a Unique, totem death, block, low Life, critical strike against a marked Unique, Energy Shield Recharge,
-- Savage Hit) summon the spirit at level 1 for its duration with its Leadership buff and fighting chieftain.
INSERT INTO catalog_entries (kind, key, payload, enabled) VALUES
('tattoo_effect', 'Loyalty Tattoo of Ahuana', '{"Name":"Loyalty Tattoo of Ahuana","Stats":[{"Stat":"has_trigger_on_suppress_hit_from_unique","Value":1}]}', 1),
('tattoo_effect', 'Loyalty Tattoo of Akoya', '{"Name":"Loyalty Tattoo of Akoya","Stats":[{"Stat":"has_trigger_on_reaching_maximum_rage_in_presence_of_unique","Value":1}]}', 1),
('tattoo_effect', 'Loyalty Tattoo of Ikiaho', '{"Name":"Loyalty Tattoo of Ikiaho","Stats":[{"Stat":"has_trigger_on_travel_skill_use_in_presence_of_unique","Value":1}]}', 1),
('tattoo_effect', 'Loyalty Tattoo of Kahuturoa', '{"Name":"Loyalty Tattoo of Kahuturoa","Stats":[{"Stat":"has_trigger_on_crit_by_unique_enemy","Value":1}]}', 1),
('tattoo_effect', 'Loyalty Tattoo of Kaom', '{"Name":"Loyalty Tattoo of Kaom","Stats":[{"Stat":"has_trigger_on_totem_death_in_presence_of_unique","Value":1}]}', 1),
('tattoo_effect', 'Loyalty Tattoo of Kiloava', '{"Name":"Loyalty Tattoo of Kiloava","Stats":[{"Stat":"has_trigger_on_block_hit_from_unique","Value":1}]}', 1),
('tattoo_effect', 'Loyalty Tattoo of Maata', '{"Name":"Loyalty Tattoo of Maata","Stats":[{"Stat":"has_trigger_on_reaching_low_life_in_presence_of_unique","Value":1}]}', 1),
('tattoo_effect', 'Loyalty Tattoo of Rakiata', '{"Name":"Loyalty Tattoo of Rakiata","Stats":[{"Stat":"has_trigger_on_crit_vs_marked_unique","Value":1}]}', 1),
('tattoo_effect', 'Loyalty Tattoo of Tawhanuku', '{"Name":"Loyalty Tattoo of Tawhanuku","Stats":[{"Stat":"has_trigger_on_es_recharge_in_presence_of_unique","Value":1}]}', 1),
('tattoo_effect', 'Loyalty Tattoo of Utula', '{"Name":"Loyalty Tattoo of Utula","Stats":[{"Stat":"has_trigger_on_taking_savage_hit_from_unique","Value":1}]}', 1);
