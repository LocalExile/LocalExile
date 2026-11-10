-- Client table SentinelTaggedMonsterStats (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SentinelTaggedMonsterStats" (
    "_row" INTEGER PRIMARY KEY,
    "TaggedStat" INTEGER,
    "__col1" INTEGER,
    "BuffTemplate" JSON,
    "RewardType1" JSON,
    "RewardType2" JSON
);
