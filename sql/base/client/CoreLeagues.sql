-- Client table CoreLeagues (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CoreLeagues" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" BOOLEAN,
    "__col2" BOOLEAN,
    "DisableChanceStat" INTEGER,
    "DisableStat" JSON,
    "LeagueChanceStat" INTEGER,
    "AdditionalLeagueChanceStat" INTEGER,
    "BaseLeagueChanceStat" INTEGER,
    "ForcedSpawnStat" JSON,
    "LeagueCountOverrideStat" INTEGER,
    "__col10" JSON,
    "__col11" INTEGER,
    "__col12" BOOLEAN,
    "__col13" INTEGER,
    "__col14" BOOLEAN,
    "__col15" BOOLEAN,
    "__col16" JSON,
    "__col17" BOOLEAN,
    "__col18" INTEGER
);
