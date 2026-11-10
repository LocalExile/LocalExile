-- Client table ChestClusters (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ChestClusters" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "ChestsKeys" JSON,
    "__col2" JSON,
    "__col3" INTEGER,
    "__col4" INTEGER,
    "__col5" INTEGER
);
