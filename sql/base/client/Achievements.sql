-- Client table Achievements (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Achievements" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Description" TEXT,
    "SetId" INTEGER,
    "Objective" TEXT,
    "HASH16" INTEGER,
    "__col5" BOOLEAN,
    "HideAchievementItems" BOOLEAN,
    "__col7" BOOLEAN,
    "MinCompletedItems" INTEGER,
    "TwoColumnLayout" BOOLEAN,
    "ShowItemCompletionsAsOne" BOOLEAN,
    "__col11" TEXT,
    "SoftcoreOnly" BOOLEAN,
    "HardcoreOnly" BOOLEAN,
    "__col14" BOOLEAN,
    "__col15" TEXT,
    "__col16" TEXT,
    "__col17" TEXT
);
