-- Oshabi, Avatar of the Grove (WorldAreas HarvestLeagueBoss, opened by the Sacred Blossom). The Harvest module reads these
-- rows: the 'unique' rows are the shares of the one guaranteed unique, every other row rolls by itself.
INSERT INTO drop_bosses (boss_id, boss_name, area_id, category, enabled) VALUES
('Metadata/Monsters/LeagueHarvest/Oshabi/OshabiBoss', 'Oshabi, Avatar of the Grove', 'HarvestLeagueBoss', 'map', 1);

INSERT INTO drop_boss_items (boss_id, item_id, item_kind, chance, confidence, source, notes) VALUES
('Metadata/Monsters/LeagueHarvest/Oshabi/OshabiBoss', 'Forbidden Shako', 'unique', 0.50, 'estimated', 'poewiki Oshabi, Avatar of the Grove (3.26-3.28 estimates, n=600)', 'share of the one guaranteed unique'),
('Metadata/Monsters/LeagueHarvest/Oshabi/OshabiBoss', 'Law of the Wilds', 'unique', 0.19, 'estimated', 'poewiki Oshabi, Avatar of the Grove (3.26-3.28 estimates, n=600)', 'share of the one guaranteed unique'),
('Metadata/Monsters/LeagueHarvest/Oshabi/OshabiBoss', 'Witchhunter''s Judgment', 'unique', 0.19, 'estimated', 'poewiki Oshabi, Avatar of the Grove (3.26-3.28 estimates, n=600)', 'share of the one guaranteed unique'),
('Metadata/Monsters/LeagueHarvest/Oshabi/OshabiBoss', 'Abhorrent Interrogation', 'unique', 0.12, 'estimated', 'poewiki Oshabi, Avatar of the Grove (3.26-3.28 estimates, n=600)', 'share of the one guaranteed unique'),
('Metadata/Monsters/LeagueHarvest/Oshabi/OshabiBoss', 'Metadata/Items/Currency/HarvestSeedBoss', 'currency', 1.0, 'estimated', 'poewiki Oshabi, Avatar of the Grove (3.26-3.28 estimates, n=600)', 'Sacred Crystallised Lifeforce, always'),
('Metadata/Monsters/LeagueHarvest/Oshabi/OshabiBoss', 'Metadata/Items/Gems/SupportGemPacifism', 'gem', 0.10, 'estimated', 'poewiki Oshabi, Avatar of the Grove (3.26-3.28 estimates, n=600)', NULL),
('Metadata/Monsters/LeagueHarvest/Oshabi/OshabiBoss', 'Metadata/Items/Gems/SupportGemGreaterUnleash', 'gem', 0.10, 'estimated', 'poewiki Oshabi, Avatar of the Grove (3.26-3.28 estimates, n=600)', NULL),
('Metadata/Monsters/LeagueHarvest/Oshabi/OshabiBoss', 'Metadata/Items/DivinationCards/DivinationCardTheAspirant', 'card', 0.14, 'estimated', 'poewiki Oshabi, Avatar of the Grove (3.26-3.28 estimates, n=600)', NULL);

INSERT INTO drop_card_sources (card_id, source_kind, source_id, min_map_tier, drop_level, confidence, source, notes) VALUES
('Metadata/Items/DivinationCards/DivinationCardTheAspirant', 'boss', 'Metadata/Monsters/LeagueHarvest/Oshabi/OshabiBoss', NULL, 0, 'estimated', 'poewiki Oshabi, Avatar of the Grove; OshabiEncounter.Drops', NULL);
