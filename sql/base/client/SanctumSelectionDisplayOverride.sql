-- Client table SanctumSelectionDisplayOverride (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SanctumSelectionDisplayOverride" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Reward" TEXT,
    "Icon" TEXT,
    "Downside" TEXT,
    "Item" INTEGER,
    "Count" INTEGER,
    "RewardCategory" INTEGER
);
