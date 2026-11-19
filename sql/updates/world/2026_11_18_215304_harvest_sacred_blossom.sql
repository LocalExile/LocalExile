-- The Sacred Blossom (opens MapDeviceRecipes HarvestBoss, Oshabi's grove) drops from the Tier 4 Sacred Grove bosses
-- (HarvestSeeds Tier 4). The Harvest module rolls the chance and scales it by the area's item quantity.
INSERT INTO drop_bosses (boss_id, boss_name, area_id, category, enabled) VALUES
('Metadata/Monsters/LeagueHarvest/Red/HarvestBearBoss', 'Ersi, Mother of Thorns', 'HarvestLeague', 'other', 1),
('Metadata/Monsters/LeagueHarvest/Green/HarvestCatBoss', 'Namharim, Born of Night', 'HarvestLeague', 'other', 1),
('Metadata/Monsters/LeagueHarvest/Blue/HarvestBirdBoss', 'Janaar, the Omen', 'HarvestLeague', 'other', 1);

INSERT INTO drop_boss_items (boss_id, item_id, item_kind, chance, confidence, source, notes) VALUES
('Metadata/Monsters/LeagueHarvest/Red/HarvestBearBoss', 'Metadata/Items/MapFragments/CurrencyHarvestBossKey', 'other', 0.12, 'estimated', 'ForgottenArbiter Harvest mechanics (3.19-3.23 samples, 43 blossoms / 360 T4 monsters, 9-16%)', 'base chance per T4 kill; Harvest scales it by 1 + area item quantity / 100 (wiki: scales with quantity)'),
('Metadata/Monsters/LeagueHarvest/Green/HarvestCatBoss', 'Metadata/Items/MapFragments/CurrencyHarvestBossKey', 'other', 0.12, 'estimated', 'ForgottenArbiter Harvest mechanics (3.19-3.23 samples, 43 blossoms / 360 T4 monsters, 9-16%)', 'base chance per T4 kill; Harvest scales it by 1 + area item quantity / 100 (wiki: scales with quantity)'),
('Metadata/Monsters/LeagueHarvest/Blue/HarvestBirdBoss', 'Metadata/Items/MapFragments/CurrencyHarvestBossKey', 'other', 0.12, 'estimated', 'ForgottenArbiter Harvest mechanics (3.19-3.23 samples, 43 blossoms / 360 T4 monsters, 9-16%)', 'base chance per T4 kill; Harvest scales it by 1 + area item quantity / 100 (wiki: scales with quantity)');
