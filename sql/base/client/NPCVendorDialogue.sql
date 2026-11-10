-- Client table NPCVendorDialogue (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "NPCVendorDialogue" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "NPC" JSON,
    "NPCAudio" INTEGER,
    "DialogueCondition" JSON,
    "__col4" INTEGER,
    "__col5" INTEGER,
    "Script" TEXT
);
