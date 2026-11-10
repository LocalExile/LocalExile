-- Client table MapRelicInventoryLayout (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MapRelicInventoryLayout" (
    "_row" INTEGER PRIMARY KEY,
    "XOffset" INTEGER,
    "YOffset" INTEGER,
    "UnlockQuest" INTEGER,
    "UnlockText" TEXT
);
