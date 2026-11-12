-- Generated table room_points (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "room_points" (
    "_row" INTEGER PRIMARY KEY,
    "room" INTEGER,
    "ord" INTEGER,
    "name" TEXT,
    "x" INTEGER,
    "y" INTEGER,
    "angle" REAL
);
