-- Client table RecipeUnlockDisplay (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "RecipeUnlockDisplay" (
    "_row" INTEGER PRIMARY KEY,
    "RecipeId" INTEGER,
    "Description" TEXT,
    "CraftingItemClassCategoriesKeys" JSON,
    "UnlockDescription" TEXT,
    "Rank" INTEGER,
    "UnlockArea" INTEGER
);
