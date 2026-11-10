-- Client table MonsterConditions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MonsterConditions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Rarity" INTEGER,
    "Stat" INTEGER,
    "NotRarity" INTEGER,
    "NotStat" JSON,
    "MapBoss" BOOLEAN,
    "NotMapBoss" BOOLEAN,
    "Tags" JSON,
    "NotTags" JSON,
    "__col9" INTEGER,
    "MinimumLevel" INTEGER,
    "MaximumLevel" INTEGER,
    "HASH32" INTEGER
);
