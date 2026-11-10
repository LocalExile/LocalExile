-- Client table MemoryLineType (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MemoryLineType" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MemoryLine" INTEGER,
    "HASH16" INTEGER,
    "Stats" JSON,
    "StatValues" JSON,
    "__col5" INTEGER,
    "Suffix" TEXT,
    "__col7" INTEGER,
    "__col8" INTEGER,
    "FinalAtlasNode" INTEGER,
    "__col10" INTEGER,
    "Tier" INTEGER,
    "CurrencyItem" INTEGER
);
