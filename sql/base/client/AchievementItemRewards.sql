-- Client table AchievementItemRewards (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AchievementItemRewards" (
    "_row" INTEGER PRIMARY KEY,
    "AchievementItemsKey" INTEGER,
    "Rewards" INTEGER,
    "Message" TEXT,
    "Id" TEXT
);
