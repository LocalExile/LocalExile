-- Client table MonsterBonuses (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MonsterBonuses" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "BonusMods" JSON,
    "BuffDefinition" INTEGER,
    "BuffStatValues" JSON,
    "StatsKeys" JSON,
    "StatValues" JSON,
    "__col6" INTEGER,
    "__col7" BOOLEAN
);
