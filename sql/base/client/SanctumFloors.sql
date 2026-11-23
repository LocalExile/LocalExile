-- Client table SanctumFloors (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SanctumFloors" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Area" INTEGER,
    "Title" INTEGER,
    "RoomIcon" TEXT,
    "BossIcon" TEXT,
    "Description" TEXT,
    "Summary" INTEGER,
    "Itemised" INTEGER
);
