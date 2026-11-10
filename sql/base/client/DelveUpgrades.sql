-- Client table DelveUpgrades (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DelveUpgrades" (
    "_row" INTEGER PRIMARY KEY,
    "DelveUpgradeTypeKey" INTEGER,
    "UpgradeLevel" INTEGER,
    "StatsKeys" JSON,
    "StatValues" JSON,
    "Cost" INTEGER,
    "__col5" INTEGER,
    "AchievementItemsKey" JSON,
    "__col7" INTEGER
);
