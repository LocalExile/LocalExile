-- Client table DamageParticleEffects (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DamageParticleEffects" (
    "_row" INTEGER PRIMARY KEY,
    "DamageParticleEffectTypes" INTEGER,
    "Variation" INTEGER,
    "PETFile" TEXT,
    "ImpactSoundData1" INTEGER,
    "ImpactSoundData2" INTEGER,
    "__col5" INTEGER
);
