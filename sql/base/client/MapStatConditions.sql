-- Client table MapStatConditions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MapStatConditions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "StatsKey" INTEGER,
    "__col2" BOOLEAN,
    "StatMin" INTEGER,
    "StatMax" INTEGER,
    "__col5" BOOLEAN
);
