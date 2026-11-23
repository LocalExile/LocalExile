-- Client table SummonedSpecificMonstersOnDeathStats (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SummonedSpecificMonstersOnDeathStats" (
    "_row" INTEGER PRIMARY KEY,
    "Id" INTEGER,
    "Monster" INTEGER,
    "__col2" BOOLEAN,
    "MapStat" INTEGER
);
