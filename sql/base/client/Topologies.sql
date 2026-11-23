-- Client table Topologies (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Topologies" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "DGRFile" TEXT,
    "__col2" INTEGER,
    "__col3" INTEGER,
    "__col4" INTEGER,
    "__col5" JSON,
    "__col6" INTEGER
);
