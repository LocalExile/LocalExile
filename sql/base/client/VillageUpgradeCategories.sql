-- Client table VillageUpgradeCategories (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "VillageUpgradeCategories" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "ScriptArgs" TEXT,
    "Name" TEXT,
    "Icon" TEXT,
    "Text" TEXT,
    "JobType" INTEGER
);
