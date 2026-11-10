-- Client table BlightCraftingRecipes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BlightCraftingRecipes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "BlightCraftingItemsKeys" JSON,
    "BlightCraftingResultsKey" INTEGER,
    "BlightCraftingTypesKey" INTEGER
);
