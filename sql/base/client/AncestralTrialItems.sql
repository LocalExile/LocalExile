-- Client table AncestralTrialItems (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AncestralTrialItems" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "HASH16" INTEGER,
    "Name" TEXT,
    "Image" TEXT,
    "Tribe" INTEGER,
    "__col5" INTEGER,
    "Mods" JSON,
    "FlavorText" TEXT,
    "__col8" INTEGER,
    "__col9" JSON,
    "SoundEffectPurchase" INTEGER,
    "SoundEffectEquip" INTEGER
);
