-- Client table DamageEffectVariations (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DamageEffectVariations" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MiscAnimated" JSON,
    "Particles" JSON,
    "__col3" INTEGER,
    "__col4" INTEGER,
    "__col5" JSON,
    "__col6" JSON,
    "EffectPack" INTEGER
);
