-- Client table MicrotransactionPortalVariations (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MicrotransactionPortalVariations" (
    "_row" INTEGER PRIMARY KEY,
    "MtxTypeGameSpecific" INTEGER,
    "Index" INTEGER,
    "AOFile" TEXT,
    "MapAOFile" TEXT,
    "__col4" REAL,
    "MiscObject" INTEGER,
    "PortalEffect" TEXT,
    "__col7" REAL,
    "OTFile" TEXT,
    "PortalEffectLarge" TEXT,
    "__col10" INTEGER,
    "__col11" INTEGER,
    "Script" TEXT
);
