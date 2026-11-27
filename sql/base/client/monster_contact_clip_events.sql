-- Generated table monster_contact_clip_events (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "monster_contact_clip_events" (
    "_row" INTEGER PRIMARY KEY,
    "clip" INTEGER,
    "ord" INTEGER,
    "name" TEXT,
    "ms" INTEGER,
    "position" JSON,
    "args" TEXT
);
