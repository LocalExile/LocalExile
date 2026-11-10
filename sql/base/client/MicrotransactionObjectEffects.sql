-- Client table MicrotransactionObjectEffects (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MicrotransactionObjectEffects" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" TEXT,
    "Script1" TEXT,
    "Script2" TEXT,
    "EffectPack" JSON,
    "MiscObject" JSON,
    "CorpseSinkVariation" INTEGER,
    "MiscAnimated" JSON,
    "__col8" BOOLEAN,
    "__col9" BOOLEAN,
    "__col10" BOOLEAN,
    "__col11" INTEGER,
    "__col12" INTEGER,
    "__col13" INTEGER,
    "__col14" JSON,
    "__col15" REAL,
    "__col16" BOOLEAN,
    "__col17" INTEGER,
    "__col18" INTEGER
);
