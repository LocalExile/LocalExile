-- Client table RitualRuneTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "RitualRuneTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MiscAnimatedKey1" INTEGER,
    "SpawnWeight" INTEGER,
    "LevelMin" INTEGER,
    "LevelMax" INTEGER,
    "BuffDefinitionsKey" INTEGER,
    "BuffStatValues" JSON,
    "SpawnPatterns" JSON,
    "ModsKey" JSON,
    "__col9" JSON,
    "__col10" JSON,
    "MiscAnimatedKey2" INTEGER,
    "EnvironmentsKey" INTEGER,
    "__col13" INTEGER,
    "Achievements" JSON,
    "Type" TEXT,
    "Description" TEXT,
    "DaemonSpawningDataKey" INTEGER,
    "__col18" BOOLEAN,
    "__col19" INTEGER
);
