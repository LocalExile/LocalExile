-- Client table Footprints (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Footprints" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Active_AOFiles" JSON,
    "Idle_AOFiles" JSON,
    "__col3" JSON,
    "__col4" JSON,
    "__col5" INTEGER,
    "MiscObject" INTEGER
);
