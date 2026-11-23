-- Client table BestiaryEncounters (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BestiaryEncounters" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MonsterPacksKey" INTEGER,
    "MonsterVarietiesKey" INTEGER,
    "MonsterSpawnerId" TEXT
);
