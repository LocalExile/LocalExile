-- Generated table animation_default_attacks (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "animation_default_attacks" (
    "_row" INTEGER PRIMARY KEY,
    "rig" INTEGER,
    "stance" TEXT,
    "ord" INTEGER,
    "animation" TEXT,
    "source" TEXT,
    "index" INTEGER,
    "begin" INTEGER,
    "length" INTEGER,
    "contact" INTEGER,
    "cancel" INTEGER,
    "slow" JSON,
    "budget" REAL,
    "weight" INTEGER,
    "item" BOOLEAN,
    "pair" BOOLEAN,
    "position" JSON
);
