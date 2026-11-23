-- Client table ItemCostPerLevel (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ItemCostPerLevel" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItem" INTEGER,
    "Level" INTEGER,
    "Cost" INTEGER,
    "CostHardmode" INTEGER
);
