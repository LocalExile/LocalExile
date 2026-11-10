-- Client table ScarabTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ScarabTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Tag" INTEGER,
    "DisableDrops" INTEGER,
    "MoreLikely" INTEGER,
    "Count" INTEGER,
    "__col5" JSON
);
