-- Client table Labyrinths: all 4 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "Labyrinths" ("_row", "Tier", "Name", "OfferingItem", "QuestFlag", "RequiredTrials", "AreaLevel", "__col6", "JewelReward", "__col8", "__col9", "MinLevel", "__col11", "__col12", "CraftingFontDescription") VALUES
(0, 1, 'The Labyrinth', NULL, 899, '[0,1,2,3,4,5]', 33, 3, 718, '[4,4,4]', '[2,2,2]', 0, 32, 32, 1019),
(1, 2, 'The Cruel Labyrinth', NULL, 992, '[6,7,8]', 55, 3, 719, '[4,4,4]', '[2,2,2]', 40, 53, 53, 1020),
(2, 3, 'The Merciless Labyrinth', NULL, 993, '[9,10,11]', 68, 3, 720, '[4,4,4]', '[2,2,2]', 50, 66, 66, 1021),
(3, 4, 'The Eternal Labyrinth', 3066, 900, '[12,13,14,15,16,17]', 75, 3, 721, '[4,4,4]', '[2,2,2]', 68, 75, 75, 1021);
