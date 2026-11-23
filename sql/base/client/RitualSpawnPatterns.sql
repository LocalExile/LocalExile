-- Client table RitualSpawnPatterns (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "RitualSpawnPatterns" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" INTEGER,
    "SpawnOrder" JSON,
    "__col3" BOOLEAN
);
