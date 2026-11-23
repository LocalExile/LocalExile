-- Client table BetrayalTargetJobAchievements (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BetrayalTargetJobAchievements" (
    "_row" INTEGER PRIMARY KEY,
    "BetrayalTargetsKey" INTEGER,
    "BetrayalJobsKey" INTEGER,
    "AchievementItemsKey" INTEGER
);
