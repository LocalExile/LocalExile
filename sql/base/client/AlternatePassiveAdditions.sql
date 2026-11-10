-- Client table AlternatePassiveAdditions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AlternatePassiveAdditions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "AlternateTreeVersionsKey" INTEGER,
    "SpawnWeight" INTEGER,
    "StatsKeys" JSON,
    "Stat1Min" INTEGER,
    "Stat1Max" INTEGER,
    "__col6" INTEGER,
    "__col7" INTEGER,
    "__col8" INTEGER,
    "__col9" INTEGER,
    "PassiveType" JSON,
    "__col11" INTEGER
);
