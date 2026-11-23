-- Client table MemoryLineModifiers (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MemoryLineModifiers" (
    "_row" INTEGER PRIMARY KEY,
    "MemoryLineType" INTEGER,
    "__col1" INTEGER,
    "Stats" JSON,
    "StatValues" JSON,
    "__col4" JSON,
    "__col5" JSON
);
