-- Client table MapCompletionAchievements (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MapCompletionAchievements" (
    "_row" INTEGER PRIMARY KEY,
    "__col0" TEXT,
    "MapStatConditionsKeys" JSON,
    "StatsKeys" JSON,
    "AchievementItemsKeys" JSON,
    "MapTierAchievementsKeys" JSON,
    "__col5" BOOLEAN,
    "WorldAreasKeys" JSON,
    "Ruleset" JSON
);
