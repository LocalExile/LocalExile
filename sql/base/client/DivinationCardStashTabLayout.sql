-- Client table DivinationCardStashTabLayout (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DivinationCardStashTabLayout" (
    "_row" INTEGER PRIMARY KEY,
    "StoredItem" INTEGER,
    "IsInGame" BOOLEAN,
    "IsEnabled" BOOLEAN
);
