-- Client table MapDeviceLayouts (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MapDeviceLayouts" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Slots" INTEGER,
    "__col2" JSON,
    "Receptacle_DDSFile" TEXT,
    "__col4" JSON,
    "__col5" JSON,
    "MapKey" INTEGER,
    "__col7" INTEGER,
    "__col8" TEXT,
    "__col9" BOOLEAN,
    "__col10" INTEGER,
    "__col11" INTEGER,
    "__col12" JSON,
    "__col13" INTEGER
);
