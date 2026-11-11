-- Client table MapStashSpecialSubStashGroup: all 14 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "MapStashSpecialSubStashGroup" ("_row", "Id", "BaseItemTypes", "Name", "__col3", "__col4", "ElderGuardian", "ShaperGuardian", "__col7", "__col8", "__col9") VALUES
(0, 'UniqueMaps', NULL, 'Unique Maps', 0, 0, NULL, NULL, NULL, 'Art/2DArt/UIImages/InGame/MTX/MapStash/MapTabUnique', NULL),
(1, 'SpecialTypes', NULL, 'Special Maps', 0, 0, NULL, NULL, NULL, 'Art/2DArt/UIImages/InGame/MTX/MapStash/MapTabElderShaperConqueror', NULL),
(2, 'Conqueror1', NULL, 'Baran, The Crusader', 0, 0, NULL, 0, NULL, '', 1),
(3, 'Conqueror2', NULL, 'Veritania, The Redeemer', 0, 0, NULL, 1, NULL, '', 1),
(4, 'Conqueror3', NULL, 'Al-Hezmin, The Hunter', 0, 0, NULL, 2, NULL, '', 1),
(5, 'Conqueror4', NULL, 'Drox, The Warlord', 0, 0, NULL, 3, NULL, '', 1),
(6, 'ElderGuardian1', NULL, 'The Enslaver', 0, 1, 0, NULL, NULL, '', 1),
(7, 'ElderGuardian2', NULL, 'The Eradicator', 0, 1, 1, NULL, NULL, '', 1),
(8, 'ElderGuardian3', NULL, 'The Constrictor', 0, 1, 2, NULL, NULL, '', 1),
(9, 'ElderGuardian4', NULL, 'The Purifier', 0, 1, 3, NULL, NULL, '', 1),
(10, 'ShaperGuardianKey', 5380, 'Shaper Guardian Map', 1, 0, NULL, NULL, NULL, '', 1),
(11, 'VaalTempleKey', 5379, 'Vaal Temple Map', 0, 0, NULL, NULL, NULL, '', 1),
(12, 'NightmareMapKey', 5381, 'Nightmare Map', 0, 0, NULL, NULL, NULL, '', 1),
(13, 'ValdoMapKey', 5378, 'Valdo Map', 0, 0, NULL, NULL, NULL, '', 1);
