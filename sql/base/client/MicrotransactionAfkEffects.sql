-- Client table MicrotransactionAfkEffects (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MicrotransactionAfkEffects" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Stat" INTEGER,
    "ObjectTask" INTEGER,
    "GrantedEffect" INTEGER,
    "__col4" INTEGER,
    "__col5" INTEGER,
    "FlyingStat" JSON,
    "__col7" BOOLEAN,
    "EffectPack" INTEGER,
    "__col9" BOOLEAN
);
