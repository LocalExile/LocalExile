-- Client table AlternateQualityTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AlternateQualityTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Description" TEXT,
    "Item" INTEGER,
    "ModEffectStat" INTEGER,
    "MapStats" JSON,
    "__col5" INTEGER,
    "IsCatalyst" BOOLEAN,
    "HASH16" INTEGER,
    "CatalystMod" INTEGER
);
