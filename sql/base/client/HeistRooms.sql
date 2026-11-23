-- Client table HeistRooms (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HeistRooms" (
    "_row" INTEGER PRIMARY KEY,
    "HeistAreasKey" INTEGER,
    "Id" INTEGER,
    "ARMFile" TEXT,
    "HeistJobsKey1" INTEGER,
    "HeistJobsKey2" INTEGER,
    "__col5" INTEGER,
    "__col6" INTEGER,
    "__col7" INTEGER,
    "__col8" TEXT,
    "__col9" INTEGER,
    "__col10" BOOLEAN,
    "__col11" BOOLEAN
);
