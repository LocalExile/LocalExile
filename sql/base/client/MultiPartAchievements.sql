-- Client table MultiPartAchievements (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MultiPartAchievements" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" INTEGER,
    "AchievementItemsKey" INTEGER,
    "Threshold" INTEGER,
    "__col4" BOOLEAN,
    "__col5" BOOLEAN,
    "__col6" INTEGER
);
