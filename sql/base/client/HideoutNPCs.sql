-- Client table HideoutNPCs (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HideoutNPCs" (
    "_row" INTEGER PRIMARY KEY,
    "Hideout_NPCsKey" INTEGER,
    "Regular_NPCsKeys" JSON,
    "HideoutDoodadsKey" INTEGER,
    "NPCMasterKey" INTEGER,
    "__col4" JSON,
    "QuestFlag" INTEGER,
    "__col6" INTEGER,
    "Quest" INTEGER,
    "__col8" INTEGER,
    "__col9" BOOLEAN,
    "Ruleset" INTEGER,
    "__col11" BOOLEAN
);
