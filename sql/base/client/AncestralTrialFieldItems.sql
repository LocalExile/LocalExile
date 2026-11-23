-- Client table AncestralTrialFieldItems (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AncestralTrialFieldItems" (
    "_row" INTEGER PRIMARY KEY,
    "Unit" INTEGER,
    "Object" INTEGER,
    "HASH32" INTEGER,
    "Description" TEXT,
    "Achievement" JSON,
    "SoundEffectPurchase" INTEGER,
    "SoundEffectPlace" INTEGER
);
