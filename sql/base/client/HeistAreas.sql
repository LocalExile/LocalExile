-- Client table HeistAreas (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HeistAreas" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "WorldAreasKeys" JSON,
    "__col2" INTEGER,
    "EnvironmentsKey1" INTEGER,
    "EnvironmentsKey2" INTEGER,
    "HeistJobsKeys" JSON,
    "Contract_BaseItemTypesKey" INTEGER,
    "Blueprint_BaseItemTypesKey" INTEGER,
    "DGRFile" TEXT,
    "__col9" INTEGER,
    "__col10" INTEGER,
    "__col11" BOOLEAN,
    "__col12" BOOLEAN,
    "Blueprint_DDSFile" TEXT,
    "Achievements1" JSON,
    "Achievements2" JSON,
    "Reward" INTEGER,
    "Achievements3" JSON,
    "RewardHardmode" INTEGER
);
