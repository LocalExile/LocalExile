-- Client table AncestralTrialTribes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AncestralTrialTribes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "NPCHub" INTEGER,
    "Portrait" TEXT,
    "TribeIcon" TEXT,
    "TribeName" TEXT,
    "FavourTracker" TEXT,
    "UnlockQuestFlag" JSON,
    "Name" TEXT,
    "QuestFlagLike" INTEGER,
    "QuestFlagDislike" INTEGER,
    "QuestFlagFightLost" INTEGER,
    "QuestFlagFightWin" INTEGER,
    "Achievement" JSON,
    "MinimapIcon" INTEGER,
    "AchievementRuthless" JSON,
    "__col15" INTEGER,
    "__col16" INTEGER,
    "__col17" INTEGER,
    "__col18" INTEGER,
    "NPCArena" INTEGER
);
