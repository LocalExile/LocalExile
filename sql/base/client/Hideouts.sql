-- Client table Hideouts (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Hideouts" (
    "_row" INTEGER PRIMARY KEY,
    "HideoutArea" INTEGER,
    "HASH16" INTEGER,
    "HideoutFile" TEXT,
    "SpawnAreas" JSON,
    "ClaimSideArea" INTEGER,
    "HideoutImage" TEXT,
    "IsEnabled" BOOLEAN,
    "Weight" INTEGER,
    "Rarity" INTEGER,
    "NotActsArea" BOOLEAN,
    "__col10" JSON,
    "__col11" BOOLEAN,
    "__col12" BOOLEAN,
    "__col13" BOOLEAN,
    "UnlockMtx" INTEGER,
    "ErrorMessage" TEXT
);
