-- Client table NPCVendorDialogueConditions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "NPCVendorDialogueConditions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Rarity" JSON,
    "ItemClass" JSON,
    "__col3" INTEGER,
    "__col4" JSON,
    "__col5" INTEGER,
    "Words" JSON
);
