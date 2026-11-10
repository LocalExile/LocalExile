-- Client table LegionFactions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "LegionFactions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "SpawnWeight" INTEGER,
    "LegionBalancePerLevelKey" INTEGER,
    "__col3" REAL,
    "__col4" REAL,
    "BuffVisualsKey" INTEGER,
    "MiscAnimatedKey1" INTEGER,
    "MiscAnimatedKey2" INTEGER,
    "MiscAnimatedKey3" INTEGER,
    "AchievementItemsKeys1" JSON,
    "MiscAnimatedKey4" INTEGER,
    "MiscAnimatedKey5" INTEGER,
    "__col12" INTEGER,
    "__col13" INTEGER,
    "AchievementItemsKeys2" JSON,
    "StatsKey" INTEGER,
    "Shard" TEXT,
    "RewardJewelArt" TEXT
);
