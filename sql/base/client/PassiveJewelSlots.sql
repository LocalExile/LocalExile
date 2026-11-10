-- Client table PassiveJewelSlots (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "PassiveJewelSlots" (
    "_row" INTEGER PRIMARY KEY,
    "Slot" INTEGER,
    "ClusterJewelSize" INTEGER,
    "ClusterIndex" INTEGER,
    "ReplacesSlot" INTEGER,
    "ProxySlot" INTEGER,
    "StartIndices" JSON,
    "__col6" BOOLEAN
);
