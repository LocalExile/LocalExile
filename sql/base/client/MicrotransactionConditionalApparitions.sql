-- Client table MicrotransactionConditionalApparitions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MicrotransactionConditionalApparitions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Stats" JSON,
    "StatValues" JSON,
    "PeriodicVariation" INTEGER,
    "__col4" INTEGER,
    "__col5" INTEGER,
    "__col6" INTEGER,
    "__col7" INTEGER,
    "__col8" INTEGER,
    "__col9" INTEGER,
    "__col10" BOOLEAN,
    "__col11" BOOLEAN
);
