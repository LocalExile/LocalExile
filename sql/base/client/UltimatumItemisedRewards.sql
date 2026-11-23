-- Client table UltimatumItemisedRewards (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "UltimatumItemisedRewards" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "HASH16" INTEGER,
    "RewardText" TEXT,
    "ItemVisualIdentityKey" JSON,
    "RewardType" INTEGER,
    "SacrificeItem" INTEGER,
    "SacrificeAmount" INTEGER,
    "SacrificeText" TEXT,
    "__col8" BOOLEAN,
    "TrialMods" JSON
);
