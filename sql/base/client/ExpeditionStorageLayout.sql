-- Client table ExpeditionStorageLayout (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ExpeditionStorageLayout" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "StoredItem" INTEGER,
    "XOffset" INTEGER,
    "YOffset" INTEGER,
    "__col4" INTEGER,
    "__col5" INTEGER,
    "Width" INTEGER,
    "Height" INTEGER,
    "__col8" INTEGER,
    "__col9" BOOLEAN,
    "Currency" INTEGER
);
