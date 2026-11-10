-- Client table NPCMaster (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "NPCMaster" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Signature_ModsKey" INTEGER,
    "SpawnWeight_TagsKeys" JSON,
    "SpawnWeight_Values" JSON,
    "Stat" JSON,
    "Achievement" JSON,
    "AfflictionAchievement" JSON,
    "League" INTEGER
);
