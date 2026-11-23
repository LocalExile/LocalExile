-- Generated table monster_contact_clips (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "monster_contact_clips" (
    "_row" INTEGER PRIMARY KEY,
    "monster" INTEGER,
    "pool" TEXT,
    "ord" INTEGER,
    "animation" TEXT,
    "contact" INTEGER,
    "index" INTEGER,
    "length" INTEGER,
    "position" JSON
);
