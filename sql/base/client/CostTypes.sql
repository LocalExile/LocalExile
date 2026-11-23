-- Client table CostTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CostTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "StatsKey" INTEGER,
    "FormatText" TEXT,
    "Divisor" INTEGER
);
