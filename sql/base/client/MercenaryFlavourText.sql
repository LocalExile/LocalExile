-- Client table MercenaryFlavourText (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MercenaryFlavourText" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Description" TEXT,
    "HASH16" INTEGER,
    "Tags" JSON,
    "TagWeight" JSON
);
