-- Client table VillageProduction (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "VillageProduction" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Input" INTEGER,
    "Output" INTEGER,
    "BaseProductionTime" INTEGER,
    "Job" INTEGER
);
