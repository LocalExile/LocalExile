-- Client table UniqueMaps (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "UniqueMaps" (
    "_row" INTEGER PRIMARY KEY,
    "ItemVisualIdentityKey" INTEGER,
    "WorldAreasKey" INTEGER,
    "WordsKey" INTEGER,
    "FlavourTextKey" INTEGER,
    "__col4" BOOLEAN,
    "Name" TEXT,
    "IsDropDisabled" BOOLEAN
);
