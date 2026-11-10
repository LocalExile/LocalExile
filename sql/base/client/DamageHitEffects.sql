-- Client table DamageHitEffects (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DamageHitEffects" (
    "_row" INTEGER PRIMARY KEY,
    "Id" INTEGER,
    "__col1" INTEGER,
    "__col2" INTEGER,
    "DamageEffectPC" JSON,
    "DamageEffectConsole" JSON
);
