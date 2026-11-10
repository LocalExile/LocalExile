-- Client table DeliriumStashTabLayout (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DeliriumStashTabLayout" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "StoredItem" INTEGER,
    "XOffset" INTEGER,
    "YOffset" INTEGER,
    "FirstSlotIndex" INTEGER,
    "Width" INTEGER,
    "Height" INTEGER,
    "SlotSize" INTEGER,
    "__col8" BOOLEAN
);
