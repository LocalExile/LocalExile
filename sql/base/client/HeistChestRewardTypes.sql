-- Client table HeistChestRewardTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HeistChestRewardTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Art" TEXT,
    "RewardTypeName" TEXT,
    "__col3" INTEGER,
    "RewardRoomName" TEXT,
    "MinLevel" INTEGER,
    "MaxLevel" INTEGER,
    "Weight" INTEGER,
    "RewardRoomName2" TEXT,
    "HeistJobsKey" JSON,
    "__col10" INTEGER,
    "__col11" INTEGER
);
