-- Client table MonsterMapBossDifficulty (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MonsterMapBossDifficulty" (
    "_row" INTEGER PRIMARY KEY,
    "MapLevel" INTEGER,
    "BossLifePercentIncrease" INTEGER,
    "BossDamagePercentIncrease" INTEGER,
    "Stat1" INTEGER,
    "Stat2" INTEGER,
    "Stat3" INTEGER,
    "BossIncItemQuantity" INTEGER,
    "Stat4" INTEGER,
    "BossIncItemRarity" INTEGER,
    "Stat5" INTEGER,
    "BossAilmentPercentDecrease" INTEGER
);
