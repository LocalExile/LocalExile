-- Client table HellscapeModificationInventoryLayout (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HellscapeModificationInventoryLayout" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Column" INTEGER,
    "Row" INTEGER,
    "IsMapSlot" BOOLEAN,
    "__col4" INTEGER,
    "Width" INTEGER,
    "Height" INTEGER,
    "Stat" INTEGER,
    "StatValue" INTEGER,
    "UnlockedWith" INTEGER,
    "Quest" INTEGER
);
