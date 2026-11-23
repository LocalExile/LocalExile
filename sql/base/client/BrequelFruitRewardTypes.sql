-- Client table BrequelFruitRewardTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BrequelFruitRewardTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "Description" TEXT,
    "__col3" TEXT,
    "__col4" TEXT,
    "__col5" JSON,
    "__col6" JSON,
    "AchievementItem" INTEGER,
    "AchievementItemHardmode" INTEGER
);
