-- Client table MonsterMapDifficulty (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MonsterMapDifficulty" (
    "_row" INTEGER PRIMARY KEY,
    "MapLevel" INTEGER,
    "LifePercentIncrease" INTEGER,
    "DamagePercentIncrease" INTEGER,
    "Stat1" INTEGER,
    "Stat2" INTEGER,
    "Stat3" JSON,
    "Stat3Value" INTEGER,
    "Stat4" JSON,
    "Stat4Value" INTEGER
);
