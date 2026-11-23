-- Client table QuestStaticRewards (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "QuestStaticRewards" (
    "_row" INTEGER PRIMARY KEY,
    "QuestFlag" INTEGER,
    "__col1" INTEGER,
    "StatsKeys" JSON,
    "StatValues" JSON,
    "QuestKey" INTEGER,
    "__col5" INTEGER,
    "ClientStringsKey" INTEGER,
    "__col7" INTEGER,
    "StatValuesHardmode" JSON,
    "ClientStringHardmode" INTEGER,
    "__col10" BOOLEAN
);
