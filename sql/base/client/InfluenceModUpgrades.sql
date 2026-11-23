-- Client table InfluenceModUpgrades (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "InfluenceModUpgrades" (
    "_row" INTEGER PRIMARY KEY,
    "InfluenceMod" INTEGER,
    "UpgradedMod" INTEGER,
    "HighestTier" BOOLEAN
);
