-- Client table CraftingSpreeType (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CraftingSpreeType" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "CurrencyItem" JSON,
    "CurrencyCount" JSON,
    "ItemClass" JSON,
    "__col4" BOOLEAN,
    "Text" TEXT,
    "__col6" INTEGER,
    "__col7" JSON
);
