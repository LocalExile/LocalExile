-- Generated table animation_clips (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "animation_clips" (
    "_row" INTEGER PRIMARY KEY,
    "rig" INTEGER,
    "name" TEXT,
    "contact" INTEGER,
    "length" INTEGER,
    "position" JSON,
    "cancel_listed" BOOLEAN,
    "cancel" INTEGER,
    "rig_index" INTEGER
);
