-- Client table ExtraTerrainFeatures (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ExtraTerrainFeatures" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "ArmFiles" JSON,
    "TdtFiles" JSON,
    "__col3" BOOLEAN,
    "__col4" JSON,
    "__col5" JSON,
    "__col6" JSON,
    "WorldAreasKey" INTEGER,
    "__col8" BOOLEAN,
    "__col9" INTEGER
);
