-- Client table SanctumDeferredRewards (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SanctumDeferredRewards" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "DeferralIcon" TEXT,
    "DeferralCategory" INTEGER,
    "RewardCategory" INTEGER,
    "Count" INTEGER
);
