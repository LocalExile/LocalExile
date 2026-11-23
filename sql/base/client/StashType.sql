-- Client table StashType (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "StashType" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "StashId" INTEGER,
    "Id2" TEXT,
    "StorageSlots" INTEGER,
    "__col4" INTEGER,
    "__col5" INTEGER,
    "Icon" TEXT
);
