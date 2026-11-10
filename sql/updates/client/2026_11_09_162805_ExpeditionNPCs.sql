-- Client table ExpeditionNPCs: all 4 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "ExpeditionNPCs" ("_row", "Id", "NPCs", "RerollItem", "__col3", "__col4", "__col5", "Faction", "Reroll", "AllBombsPlaced", "BombPlacedRemnant", "BombPlacedTreasure", "BombPlacedMonsters", "BombPlacedGeneric", "EncounterComplete", "__col14", "__col15") VALUES
(0, 'Gambler', '[512,516,520]', 4220, 16, 16, 2407, 0, 12191, 12264, 12246, 12255, 12237, 12234, 12588, 10, 10),
(1, 'Haggler', '[513,517,521]', 4218, 13, 16, 2408, 1, 12315, 12345, 12333, 12339, 12327, 12325, 12582, 4, 6),
(2, 'Dealer', '[514,518,522]', 4219, 13, 16, 2409, 2, 12025, 12069, 12057, 12063, 12051, 12049, 12600, 6, 8),
(3, 'Saga', '[511,515,519]', 4221, 3, 5, 2410, 3, 11886, 11867, 11849, 11855, 11861, NULL, 12603, 3, 5);
