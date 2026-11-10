-- Client table CorpseSinkVariations (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CorpseSinkVariations" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "AOFile" TEXT,
    "EPKFile" TEXT,
    "AOFiles" JSON,
    "__col4" INTEGER,
    "__col5" BOOLEAN,
    "__col6" INTEGER,
    "__col7" INTEGER,
    "__col8" BOOLEAN
);
