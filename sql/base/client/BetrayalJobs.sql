-- Client table BetrayalJobs (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BetrayalJobs" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Text" TEXT,
    "ExtraTerrainFeaturesKey" INTEGER,
    "IconArt" TEXT,
    "__col4" INTEGER,
    "__col5" INTEGER,
    "WorldAreasKey" INTEGER,
    "Completion_AchievementItemsKey" JSON,
    "OpenChests_AchievementItemsKey" JSON,
    "MissionCompletion_AchievementItemsKey" JSON,
    "Art" TEXT
);
