-- Generated table monster_melee_clocks (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "monster_melee_clocks" (
    "_row" INTEGER PRIMARY KEY,
    "monster" INTEGER,
    "ord" INTEGER,
    "act" TEXT,
    "amd" TEXT,
    "animation" TEXT,
    "ao" TEXT,
    "budget" REAL,
    "clock" BOOLEAN,
    "contact" INTEGER,
    "index" INTEGER,
    "length" INTEGER,
    "slow_begin" INTEGER,
    "slow_end" INTEGER
);
