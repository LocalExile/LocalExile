-- Client table Incubators (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Incubators" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" INTEGER,
    "__col1" INTEGER,
    "Description" TEXT,
    "HASH16" INTEGER,
    "AchievementItemsKeys" JSON
);
