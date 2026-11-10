-- Client table AtlasPrimordialBossInfluence (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AtlasPrimordialBossInfluence" (
    "_row" INTEGER PRIMARY KEY,
    "Boss" INTEGER,
    "Progress" INTEGER,
    "MinMapTier" INTEGER,
    "__col3" INTEGER,
    "__col4" INTEGER,
    "QuestItem" INTEGER,
    "__col6" REAL,
    "QuestFlag" INTEGER
);
