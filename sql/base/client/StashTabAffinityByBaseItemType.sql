-- Client table StashTabAffinityByBaseItemType (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "StashTabAffinityByBaseItemType" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemType" INTEGER,
    "StashTabAffinityId" JSON
);
