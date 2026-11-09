-- Client table HeistNPCs (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HeistNPCs" (
    "_row" INTEGER PRIMARY KEY,
    "NPCsKey" INTEGER,
    "MonsterVarietiesKey" INTEGER,
    "SkillLevel_HeistJobsKeys" JSON,
    "PortraitFile" TEXT,
    "HeistNPCStatsKeys" JSON,
    "StatValues" JSON,
    "__col6" REAL,
    "SkillLevel_Values" JSON,
    "Name" TEXT,
    "SilhouetteFile" TEXT,
    "__col10" INTEGER,
    "__col11" INTEGER,
    "HeistNPCsKey" INTEGER,
    "StatValues2" JSON,
    "Ally_NPCsKey" INTEGER,
    "ActiveNPCIcon" TEXT,
    "HeistJobsKey" INTEGER,
    "Equip_AchievementItemsKeys" JSON,
    "AOFile" TEXT,
    "Inventory" INTEGER
);
