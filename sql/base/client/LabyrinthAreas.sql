-- Client table LabyrinthAreas (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "LabyrinthAreas" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Normal_WorldAreasKeys" JSON,
    "Cruel_WorldAreasKeys" JSON,
    "Merciless_WorldAreasKeys" JSON,
    "Endgame_WorldAreasKeys" JSON,
    "__col5" INTEGER
);
