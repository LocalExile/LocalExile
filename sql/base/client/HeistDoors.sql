-- Client table HeistDoors (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HeistDoors" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" TEXT,
    "HeistJobsKey1" INTEGER,
    "HeistJobsKey2" INTEGER,
    "__col4" TEXT,
    "__col5" JSON,
    "__col6" JSON,
    "__col7" INTEGER,
    "HeistAreasKey" INTEGER
);
