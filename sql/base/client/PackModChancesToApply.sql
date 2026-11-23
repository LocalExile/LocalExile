-- Client table PackModChancesToApply (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "PackModChancesToApply" (
    "_row" INTEGER PRIMARY KEY,
    "Mod" INTEGER,
    "Chance" INTEGER,
    "Guaranteed" BOOLEAN
);
