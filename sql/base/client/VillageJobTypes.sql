-- Client table VillageJobTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "VillageJobTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Icon" TEXT,
    "StateText" TEXT,
    "MaxWorkersStat" INTEGER,
    "__col4" BOOLEAN,
    "JobSpeedStat" INTEGER
);
