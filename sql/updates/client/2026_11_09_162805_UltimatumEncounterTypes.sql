-- Client table UltimatumEncounterTypes: all 5 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "UltimatumEncounterTypes" ("_row", "Id", "Name", "ProgressBarText", "__col3", "__col4", "NormalAchievements", "InscribedAchievement", "EncounterChanceStat") VALUES
(0, 'Exterminate', 'Defeat waves of enemies', 'Monsters Slain', 1, 0, '[7715]', 7908, 19243),
(1, 'Survival', 'Survive', 'Survival Duration', 1, 0, '[7716]', 7909, 19244),
(2, 'Defense', 'Protect the Altar', 'Attackers Slain', 1, 1, '[7718]', 7910, 19242),
(3, 'Conquer', 'Stand in the Stone Circles', 'Stone Circles Conquered', 1, 0, '[7717]', 7911, 19245),
(4, 'Boss', 'Boss', '', 0, 0, '[]', NULL, NULL);
