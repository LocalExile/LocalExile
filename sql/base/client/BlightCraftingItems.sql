-- Client table BlightCraftingItems (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BlightCraftingItems" (
    "_row" INTEGER PRIMARY KEY,
    "Oil" INTEGER,
    "Tier" INTEGER,
    "Achievements" JSON,
    "UseType" INTEGER,
    "NameShort" TEXT
);
