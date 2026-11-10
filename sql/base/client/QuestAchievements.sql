-- Client table QuestAchievements (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "QuestAchievements" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "QuestStates" JSON,
    "__col2" JSON,
    "AchievementItems" JSON,
    "NPCs" JSON,
    "__col5" BOOLEAN
);
