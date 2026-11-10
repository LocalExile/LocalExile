-- Client table SynthesisRewardTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SynthesisRewardTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Description" TEXT,
    "ArtFile" TEXT,
    "AchievementItemsKey" JSON
);
