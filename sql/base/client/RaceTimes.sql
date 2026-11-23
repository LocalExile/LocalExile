-- Client table RaceTimes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "RaceTimes" (
    "_row" INTEGER PRIMARY KEY,
    "RacesKey" INTEGER,
    "Index" INTEGER,
    "StartUNIXTime" INTEGER,
    "EndUNIXTime" INTEGER
);
