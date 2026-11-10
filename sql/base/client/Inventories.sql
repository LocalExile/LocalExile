-- Client table Inventories (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Inventories" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "InventoryIdKey" INTEGER,
    "InventoryTypeKey" INTEGER,
    "__col3" BOOLEAN,
    "__col4" BOOLEAN,
    "__col5" INTEGER,
    "__col6" BOOLEAN,
    "__col7" INTEGER,
    "__col8" JSON
);
