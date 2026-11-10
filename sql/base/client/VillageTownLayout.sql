-- Client table VillageTownLayout (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "VillageTownLayout" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Job" INTEGER,
    "JobType" INTEGER,
    "MapButtonArt" TEXT,
    "Name" TEXT,
    "PositionX" INTEGER,
    "PositionY" INTEGER,
    "Resource" INTEGER
);
