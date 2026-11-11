-- The eleven drop-disabled Honoured rows of issue #372 whose effects had no server data yet: the nine attribute
-- notable replacements and the two small-attribute ones (Flock, Hatungo). Each row is the replacement passive's
-- canonical stat set from the client's PassiveSkillOverrides (Stats + StatValues).
-- Consumers: maximum-resistance and charge-cap stats (ChargePanelStats, the resistance cap), fortify_on_hit
-- (FortificationOnHitLocked), non_curse_aura_effect_+% (Progression/GemSupports), base_reservation_efficiency_+%
-- (Reservations/Progression), the attribute gem-level stats (GemLevels.EffectiveGemLevel), and the two on-hit buff
-- stats gain_onslaught_on_hit_duration_ms / gain_arcane_surge_on_hit_%_chance (Game.OnslaughtOnHitLocked,
-- Game.ArcaneSurgeOnSpellHitLocked).
INSERT INTO catalog_entries (kind, key, payload, enabled) VALUES
('tattoo_effect', 'Honoured Tattoo of the Barbarian', '{"Name":"Honoured Tattoo of the Barbarian","Stats":[{"Stat":"base_maximum_cold_damage_resistance_%","Value":1},{"Stat":"max_frenzy_charges","Value":1}]}', 1),
('tattoo_effect', 'Honoured Tattoo of the Berserker', '{"Name":"Honoured Tattoo of the Berserker","Stats":[{"Stat":"gain_onslaught_on_hit_duration_ms","Value":4000}]}', 1),
('tattoo_effect', 'Honoured Tattoo of the Flock', '{"Name":"Honoured Tattoo of the Flock","Stats":[{"Stat":"non_curse_aura_effect_+%","Value":1}]}', 1),
('tattoo_effect', 'Honoured Tattoo of the Flood', '{"Name":"Honoured Tattoo of the Flood","Stats":[{"Stat":"gain_arcane_surge_on_hit_%_chance","Value":100}]}', 1),
('tattoo_effect', 'Honoured Tattoo of the Hatungo', '{"Name":"Honoured Tattoo of the Hatungo","Stats":[{"Stat":"base_reservation_efficiency_+%","Value":1}]}', 1),
('tattoo_effect', 'Honoured Tattoo of the Hunter', '{"Name":"Honoured Tattoo of the Hunter","Stats":[{"Stat":"dexterity_skill_gem_level_+","Value":1}]}', 1),
('tattoo_effect', 'Honoured Tattoo of the Mountain', '{"Name":"Honoured Tattoo of the Mountain","Stats":[{"Stat":"base_maximum_fire_damage_resistance_%","Value":1},{"Stat":"max_endurance_charges","Value":1}]}', 1),
('tattoo_effect', 'Honoured Tattoo of the Pa', '{"Name":"Honoured Tattoo of the Pa","Stats":[{"Stat":"fortify_on_hit","Value":1}]}', 1),
('tattoo_effect', 'Honoured Tattoo of the Storm', '{"Name":"Honoured Tattoo of the Storm","Stats":[{"Stat":"base_maximum_lightning_damage_resistance_%","Value":1},{"Stat":"max_power_charges","Value":1}]}', 1),
('tattoo_effect', 'Honoured Tattoo of the Warlord', '{"Name":"Honoured Tattoo of the Warlord","Stats":[{"Stat":"strength_skill_gem_level_+","Value":1}]}', 1),
('tattoo_effect', 'Honoured Tattoo of the Wise', '{"Name":"Honoured Tattoo of the Wise","Stats":[{"Stat":"intelligence_skill_gem_level_+","Value":1}]}', 1);
