-- Client table GraphicalItemExchangeSlot (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "GraphicalItemExchangeSlot" (
    "_row" INTEGER PRIMARY KEY,
    "Item" INTEGER,
    "__col1" INTEGER,
    "Inventory" INTEGER,
    "__col3" INTEGER,
    "__col4" INTEGER,
    "__col5" INTEGER,
    "__col6" INTEGER,
    "__col7" INTEGER,
    "__col8" INTEGER,
    "__col9" INTEGER,
    "__col10" BOOLEAN,
    "__col11" BOOLEAN,
    "ItemCondition" JSON,
    "GlowArt" TEXT,
    "__col14" INTEGER,
    "__col15" TEXT,
    "__col16" BOOLEAN,
    "__col17" BOOLEAN
);
