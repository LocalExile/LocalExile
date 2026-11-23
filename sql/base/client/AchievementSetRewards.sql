-- Client table AchievementSetRewards (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AchievementSetRewards" (
    "_row" INTEGER PRIMARY KEY,
    "SetId" INTEGER,
    "AchievementsRequired" INTEGER,
    "Rewards" JSON,
    "TotemPieceEveryNAchievements" INTEGER,
    "Message" TEXT,
    "NotificationIcon" TEXT,
    "HideoutName" TEXT,
    "Id" TEXT
);
