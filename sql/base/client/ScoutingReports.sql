-- Client table ScoutingReports (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ScoutingReports" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "BaseItemType" INTEGER,
    "MinMapTier" INTEGER
);
