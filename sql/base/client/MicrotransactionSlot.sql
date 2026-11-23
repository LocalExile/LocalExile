-- Client table MicrotransactionSlot (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MicrotransactionSlot" (
    "_row" INTEGER PRIMARY KEY,
    "Id" INTEGER,
    "ShopTag" INTEGER,
    "Name" TEXT,
    "InventorySlot" INTEGER,
    "FlaskSlot" INTEGER,
    "__col5" BOOLEAN,
    "__col6" INTEGER,
    "__col7" INTEGER,
    "__col8" BOOLEAN,
    "__col9" INTEGER
);
