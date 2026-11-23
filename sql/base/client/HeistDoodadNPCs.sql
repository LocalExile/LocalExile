-- Client table HeistDoodadNPCs (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HeistDoodadNPCs" (
    "_row" INTEGER PRIMARY KEY,
    "NPCsKey" INTEGER,
    "QuestFlag1" INTEGER,
    "QuestFlag2" INTEGER,
    "__col3" INTEGER,
    "__col4" INTEGER,
    "__col5" INTEGER,
    "AOFile" TEXT,
    "Stance" TEXT,
    "BetrayalTargetsKey" INTEGER
);
