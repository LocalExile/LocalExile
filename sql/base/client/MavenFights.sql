-- Client table MavenFights (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MavenFights" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "WitnessesRequired" INTEGER,
    "AreaLevel" INTEGER,
    "__col3" INTEGER,
    "__col4" INTEGER,
    "BaseItemType" INTEGER,
    "__col6" INTEGER,
    "MinMapTier" INTEGER,
    "__col8" INTEGER,
    "__col9" INTEGER,
    "WitnessAreas" JSON,
    "__col11" INTEGER,
    "__col12" BOOLEAN,
    "__col13" JSON,
    "Achievements1" JSON,
    "Achievements2" JSON
);
