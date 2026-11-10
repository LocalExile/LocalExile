-- Client table HeistStorageLayout (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HeistStorageLayout" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "StoredItem" INTEGER,
    "__col2" BOOLEAN,
    "ButtonFile" TEXT,
    "SlotSize" INTEGER,
    "HeistJobsKey" INTEGER,
    "Width" INTEGER,
    "Height" INTEGER,
    "__col8" INTEGER,
    "__col9" INTEGER,
    "__col10" INTEGER,
    "__col11" INTEGER,
    "ItemClass" INTEGER
);
