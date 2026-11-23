-- Client table TieredMicrotransactionsVisuals (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "TieredMicrotransactionsVisuals" (
    "_row" INTEGER PRIMARY KEY,
    "MTX" INTEGER,
    "Tier" INTEGER,
    "Description" TEXT,
    "DDSFile" TEXT,
    "__col4" INTEGER,
    "__col5" TEXT
);
