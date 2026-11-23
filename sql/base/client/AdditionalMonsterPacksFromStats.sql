-- Client table AdditionalMonsterPacksFromStats (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AdditionalMonsterPacksFromStats" (
    "_row" INTEGER PRIMARY KEY,
    "StatsKey" INTEGER,
    "__col1" INTEGER,
    "MonsterPacksKeys" JSON,
    "AdditionalMonsterPacksStatMode" INTEGER,
    "PackCountStatsKey" INTEGER,
    "StatsKeys" JSON,
    "StatsValues" JSON,
    "__col7" INTEGER,
    "PackSizeStatsKey" INTEGER,
    "T17SpawnChanceStatsKey" INTEGER
);
