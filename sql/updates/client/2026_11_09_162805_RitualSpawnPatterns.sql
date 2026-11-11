-- Client table RitualSpawnPatterns: all 5 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "RitualSpawnPatterns" ("_row", "Id", "__col1", "SpawnOrder", "__col3") VALUES
(0, 'SpawnOutsideRandom', 0, '["Outside"]', 1),
(1, 'Circle', 3, '["Outside"]', 1),
(2, 'OutsideFirst', 1, '["Outside","Inside"]', 1),
(3, 'SpawnInsideRandom', 0, '["Inside"]', 1),
(4, 'InsideFirst', 2, '["Inside","Outside"]', 1);
