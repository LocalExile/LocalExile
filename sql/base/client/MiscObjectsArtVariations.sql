-- Client table MiscObjectsArtVariations (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MiscObjectsArtVariations" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MiscObject" JSON,
    "__col2" INTEGER,
    "__col3" INTEGER,
    "Stat" INTEGER
);
