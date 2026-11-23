-- Generated table delve_path_spawns (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "delve_path_spawns" (
    "_row" INTEGER PRIMARY KEY,
    "room" INTEGER,
    "ord" INTEGER,
    "X" INTEGER,
    "Y" INTEGER,
    "Angle" REAL,
    "Name" TEXT,
    "Section" INTEGER
);
