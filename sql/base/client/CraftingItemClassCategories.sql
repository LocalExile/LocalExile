-- Client table CraftingItemClassCategories (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CraftingItemClassCategories" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "ItemClasses" JSON,
    "CategoryCode" TEXT,
    "Text" TEXT,
    "CraftingBenchTypes" JSON
);
