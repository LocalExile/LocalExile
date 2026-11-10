-- Client table Pet (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Pet" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MtxTypeGameSpecificKey" INTEGER,
    "HASH32" INTEGER,
    "HASH16" INTEGER,
    "__col4" INTEGER,
    "__col5" BOOLEAN,
    "__col6" BOOLEAN,
    "__col7" JSON,
    "HideoutCategory" INTEGER,
    "Skills" JSON,
    "__col10" INTEGER,
    "__col11" INTEGER,
    "__col12" BOOLEAN,
    "__col13" BOOLEAN,
    "MiscObject" INTEGER,
    "DropEffect" INTEGER
);
