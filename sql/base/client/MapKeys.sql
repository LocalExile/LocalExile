-- Client table MapKeys (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MapKeys" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemType" INTEGER,
    "MapTier" INTEGER,
    "KeyTier" INTEGER,
    "IsSpecial" BOOLEAN
);
