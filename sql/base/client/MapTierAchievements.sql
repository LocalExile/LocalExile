-- Client table MapTierAchievements (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MapTierAchievements" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "AchievementItemsKey" JSON,
    "MapTiers" JSON
);
