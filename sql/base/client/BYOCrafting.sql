-- Client table BYOCrafting (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BYOCrafting" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Description" TEXT,
    "Action" TEXT,
    "DescriptionRuthless" TEXT,
    "ActionRuthless" TEXT,
    "AchievementItems" JSON
);
