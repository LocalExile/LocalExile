-- Client table NPCs (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "NPCs" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "Metadata" TEXT,
    "QuestFlags" INTEGER,
    "NPCMasterKey" INTEGER,
    "ShortName" TEXT,
    "__col6" INTEGER,
    "NPCAudios1" JSON,
    "NPCAudios2" JSON,
    "HASH16" INTEGER,
    "__col10" INTEGER,
    "Portrait" INTEGER,
    "DialogueStyle" INTEGER,
    "__col13" BOOLEAN,
    "ShopSet" INTEGER,
    "Gender" TEXT,
    "__col16" BOOLEAN,
    "__col17" INTEGER
);
