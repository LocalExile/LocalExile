-- Client table BloodTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BloodTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "ImpactSoundCrit" INTEGER,
    "ImpactSoundGoreCrit" INTEGER,
    "Animation" JSON,
    "ImpactSoundGoreHit" INTEGER,
    "__col5" JSON,
    "__col6" JSON,
    "EffectSmall" INTEGER,
    "EffectLarge" INTEGER,
    "EffectCrit" INTEGER,
    "EffectGoreSmall" INTEGER,
    "EffectGoreLarge" INTEGER,
    "EffectGoreCrit" INTEGER,
    "EffectFirstBloodSmall" INTEGER,
    "EffectFirstBloodLarge" INTEGER,
    "EffectFirstBloodCrit" INTEGER
);
