-- Generated table monster_takehit (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "monster_takehit" (
    "_row" INTEGER PRIMARY KEY,
    "variety" TEXT,
    "act" TEXT,
    "amd" TEXT,
    "clips" JSON,
    "death_clips" JSON,
    "death_unblock" INTEGER,
    "index" INTEGER,
    "indexes" JSON,
    "lengths" JSON,
    "emerge_clip" TEXT,
    "emerge_index" INTEGER,
    "emerge_length" INTEGER,
    "death_unblockByKind_DeathFromDegen" INTEGER
);
