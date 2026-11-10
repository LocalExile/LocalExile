-- Client table AchievementItems (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AchievementItems" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" INTEGER,
    "__col2" INTEGER,
    "Name" TEXT,
    "CompletionsRequired" INTEGER,
    "AchievementsKey" INTEGER,
    "__col6" BOOLEAN,
    "__col7" BOOLEAN,
    "__col8" BOOLEAN,
    "__col9" BOOLEAN
);
