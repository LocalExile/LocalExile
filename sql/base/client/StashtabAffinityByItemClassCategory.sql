-- Client table StashtabAffinityByItemClassCategory (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "StashtabAffinityByItemClassCategory" (
    "_row" INTEGER PRIMARY KEY,
    "ItemClassCategory" INTEGER,
    "StashTabAffinityId" INTEGER
);
