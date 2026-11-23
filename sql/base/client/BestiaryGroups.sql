-- Client table BestiaryGroups (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BestiaryGroups" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Description" TEXT,
    "Illustration" TEXT,
    "Name" TEXT,
    "Icon" TEXT,
    "IconSmall" TEXT,
    "BestiaryFamiliesKey" INTEGER,
    "AchievementItemsKeys" JSON
);
