-- Client table UltimatumEncounters (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "UltimatumEncounters" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Description" TEXT,
    "ModTypes" JSON,
    "BossARMFile" TEXT,
    "Type" INTEGER,
    "Icon" TEXT,
    "HASH16" INTEGER,
    "__col7" BOOLEAN,
    "__col8" INTEGER,
    "__col9" BOOLEAN
);
