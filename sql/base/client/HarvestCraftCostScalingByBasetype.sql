-- Client table HarvestCraftCostScalingByBasetype (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HarvestCraftCostScalingByBasetype" (
    "_row" INTEGER PRIMARY KEY,
    "Craft" INTEGER,
    "BaseItemType" INTEGER,
    "Scale" INTEGER
);
