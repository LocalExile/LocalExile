-- Client table MicrotransactionLevelUpEffects (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MicrotransactionLevelUpEffects" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Animation" INTEGER,
    "EffectPack" INTEGER,
    "Stat" INTEGER,
    "MiscObject" INTEGER,
    "__col5" TEXT,
    "Script" TEXT,
    "__col7" TEXT,
    "SoundEffect" INTEGER
);
