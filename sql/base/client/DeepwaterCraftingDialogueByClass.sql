-- Client table DeepwaterCraftingDialogueByClass (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DeepwaterCraftingDialogueByClass" (
    "_row" INTEGER PRIMARY KEY,
    "ItemClass" INTEGER,
    "NPCDialogue" JSON,
    "DialogueWeighting" JSON,
    "__col3" INTEGER
);
