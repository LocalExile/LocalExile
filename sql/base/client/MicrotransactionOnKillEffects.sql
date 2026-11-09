-- Client table MicrotransactionOnKillEffects (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MicrotransactionOnKillEffects" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "ObjectEffect1" INTEGER,
    "ObjectEffect2" INTEGER,
    "KillCondition" INTEGER,
    "HASH32" INTEGER,
    "OnKillBeam" INTEGER,
    "__col6" INTEGER,
    "Stat" INTEGER,
    "Counter" JSON,
    "__col9" JSON
);
