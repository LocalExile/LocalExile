-- Client table SpawnAdditionalChestsOrClusters (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SpawnAdditionalChestsOrClusters" (
    "_row" INTEGER PRIMARY KEY,
    "StatsKey" INTEGER,
    "ChestsKey" INTEGER,
    "ChestClustersKey" INTEGER
);
