-- Client table SanctumDeferredRewardCategories (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SanctumDeferredRewardCategories" (
    "_row" INTEGER PRIMARY KEY,
    "Item" INTEGER,
    "NamePlural" TEXT,
    "ScriptReward" TEXT
);
