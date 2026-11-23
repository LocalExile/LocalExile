-- Client table FragmentStashTabLayout (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "FragmentStashTabLayout" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "XOffset" INTEGER,
    "YOffset" INTEGER,
    "FirstSlotIndex" INTEGER,
    "Width" INTEGER,
    "Height" INTEGER,
    "__col6" BOOLEAN,
    "Tab" INTEGER,
    "SlotSize" INTEGER,
    "HideIfEmpty" BOOLEAN,
    "Subtab" INTEGER,
    "StoredItems" JSON,
    "__col12" BOOLEAN,
    "__col13" TEXT,
    "__col14" INTEGER,
    "__col15" BOOLEAN
);
