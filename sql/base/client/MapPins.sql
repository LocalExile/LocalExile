-- Client table MapPins (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MapPins" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "PositionX" INTEGER,
    "PositionY" INTEGER,
    "Waypoint_WorldAreasKey" INTEGER,
    "WorldAreasKeys" JSON,
    "Name" TEXT,
    "FlavourText" TEXT,
    "QuestFlags" JSON,
    "Act" INTEGER,
    "__col9" TEXT,
    "QuestFlagVisit" JSON,
    "QuestFlagKill" INTEGER,
    "__col12" JSON
);
