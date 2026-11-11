-- Client table MicrotransactionCounters (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MicrotransactionCounters" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "CountInterval" INTEGER,
    "HASH32" INTEGER,
    "FunctionCall" TEXT
);
