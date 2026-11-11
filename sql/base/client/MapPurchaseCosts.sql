-- Client table MapPurchaseCosts (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MapPurchaseCosts" (
    "_row" INTEGER PRIMARY KEY,
    "Tier" INTEGER,
    "Cost" INTEGER,
    "CostHardmode" INTEGER
);
