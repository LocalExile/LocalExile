-- Generated table room_doodads (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "room_doodads" (
    "_row" INTEGER PRIMARY KEY,
    "room" INTEGER,
    "ord" INTEGER,
    "model" TEXT,
    "x" INTEGER,
    "y" INTEGER,
    "type" TEXT,
    "angle" REAL,
    "scale" REAL,
    "z" REAL
);
