-- Client table KillstreakThresholds (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "KillstreakThresholds" (
    "_row" INTEGER PRIMARY KEY,
    "Kills" INTEGER,
    "MonsterVarietiesKey" INTEGER,
    "AchievementItemsKey" INTEGER
);
