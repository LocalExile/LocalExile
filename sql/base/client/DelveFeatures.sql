-- Client table DelveFeatures (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DelveFeatures" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "SpawnWeight" JSON,
    "WorldAreasKey" INTEGER,
    "Image" TEXT,
    "AchievementItemsKeys" JSON,
    "__col6" JSON,
    "MinDepth" JSON,
    "Description" TEXT,
    "__col9" INTEGER,
    "__col10" JSON,
    "__col11" JSON,
    "__col12" JSON,
    "__col13" JSON,
    "__col14" JSON,
    "__col15" JSON
);
