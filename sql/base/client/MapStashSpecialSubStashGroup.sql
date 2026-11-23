-- Client table MapStashSpecialSubStashGroup (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MapStashSpecialSubStashGroup" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "BaseItemTypes" INTEGER,
    "Name" TEXT,
    "__col3" BOOLEAN,
    "__col4" BOOLEAN,
    "ElderGuardian" INTEGER,
    "ShaperGuardian" INTEGER,
    "__col7" JSON,
    "__col8" TEXT,
    "__col9" INTEGER
);
