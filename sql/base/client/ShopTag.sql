-- Client table ShopTag (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ShopTag" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "IsCategory" BOOLEAN,
    "Category" INTEGER,
    "Game2" BOOLEAN,
    "__col5" TEXT,
    "Guild" BOOLEAN,
    "__col7" INTEGER,
    "__col8" TEXT
);
