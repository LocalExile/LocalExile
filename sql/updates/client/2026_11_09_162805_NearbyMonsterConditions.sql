-- Client table NearbyMonsterConditions: all 13 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "NearbyMonsterConditions" ("_row", "Id", "MonsterVarietiesKeys", "MonsterAmount", "__col3", "IsNegated", "__col5", "__col6", "IsLessThen", "MinimumHealthPercentage") VALUES
(0, 'HasIceTotemsX4', '[1096]', 4, 0, 0, 200, '[]', 0, 1),
(1, 'HasIceMinionsX10', '[4432,4433]', 10, 0, 0, 200, '[]', 0, 1),
(2, 'HasBloodRavensX3', '[3262]', 3, 0, 0, 200, '[]', 0, 1),
(3, 'HasFireSkelliesX10', '[2398]', 10, 0, 0, 200, '[]', 0, 1),
(4, 'HasNoUnmovedWickermanStatues', '[5481]', 0, 0, 1, 200, '[]', 0, 1),
(5, 'HasNoUnmovedStatues', '[5480]', 0, 0, 1, 200, '[]', 0, 1),
(6, 'HasVoidPitsX12', '[]', 12, 0, 0, 200, '[398]', 0, 1),
(7, 'HasPylonsX12', '[]', 12, 0, 0, 200, '[556]', 0, 1),
(8, 'HasLessThan7VoidPits', '[]', 7, 0, 0, 200, '[398]', 1, 1),
(9, 'HasLessThan7Pylons', '[]', 7, 0, 0, 200, '[556]', 1, 1),
(10, 'HasHeraldOfAshOverHalfHealth', '[2555]', 1, 0, 0, 200, '[]', 0, 50),
(11, 'HasSanctumBossBraomOverHalfHealth', '[9775]', 1, 0, 0, 1000, '[]', 0, 50),
(12, 'HasSanctumBossUzarOverHalfHealth', '[9776]', 1, 0, 0, 1000, '[]', 0, 50);
