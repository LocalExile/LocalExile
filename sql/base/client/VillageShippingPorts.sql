-- Client table VillageShippingPorts (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "VillageShippingPorts" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "FlagArt" TEXT,
    "MapLineArt" TEXT,
    "ReturnMessage" TEXT,
    "__col5" BOOLEAN,
    "PositionX" INTEGER,
    "PositionY" INTEGER,
    "__col8" INTEGER,
    "__col9" INTEGER,
    "DistanceFromPort" INTEGER,
    "__col11" BOOLEAN,
    "RiskMultiplier" REAL,
    "__col13" JSON
);
