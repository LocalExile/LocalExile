-- Client table HarvestCraftOptions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HarvestCraftOptions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Text" TEXT,
    "Tier" INTEGER,
    "Command" TEXT,
    "Parameters" TEXT,
    "MirageAchievements" JSON,
    "HASH16" INTEGER,
    "Description" TEXT,
    "IsEnchant" BOOLEAN,
    "LifeforceType" INTEGER,
    "LifeforceCost" INTEGER,
    "SacredCost" INTEGER,
    "IsProportionalToStackSize" BOOLEAN,
    "NecropolisAchievements" JSON,
    "GameMode" INTEGER,
    "RancourCost" INTEGER
);
