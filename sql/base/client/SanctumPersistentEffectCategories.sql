-- Client table SanctumPersistentEffectCategories (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SanctumPersistentEffectCategories" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Frame" TEXT,
    "Popup" TEXT,
    "Glow" TEXT,
    "Curse" BOOLEAN,
    "Boon" BOOLEAN,
    "Icon" TEXT,
    "Name" TEXT,
    "Deferral" BOOLEAN
);
