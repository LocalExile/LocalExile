-- Client table ExpeditionRelics (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ExpeditionRelics" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "ItemTag" INTEGER,
    "AOFile" TEXT,
    "MinLevel" INTEGER,
    "MaxLevel" INTEGER
);
