-- Client table BlightTowers (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BlightTowers" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "Description" TEXT,
    "Icon" TEXT,
    "NextUpgradeOptions" JSON,
    "__col5" INTEGER,
    "Tier" TEXT,
    "Radius" INTEGER,
    "__col8" INTEGER,
    "SpendResourceAchievement" JSON,
    "StatsKey" INTEGER,
    "StatsKeys" JSON,
    "StatsKeys2" JSON,
    "__col13" BOOLEAN
);
