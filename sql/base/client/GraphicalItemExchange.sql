-- Client table GraphicalItemExchange (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "GraphicalItemExchange" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "BackgroundArt" TEXT,
    "Red" INTEGER,
    "Green" INTEGER,
    "Blue" INTEGER,
    "__col5" INTEGER,
    "__col6" INTEGER,
    "__col7" INTEGER,
    "ButtonLabel" INTEGER,
    "__col9" JSON,
    "__col10" INTEGER,
    "__col11" BOOLEAN
);
