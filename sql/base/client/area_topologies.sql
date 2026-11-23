-- Generated table area_topologies (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "area_topologies" (
    "_row" INTEGER PRIMARY KEY,
    "area" INTEGER,
    "ord" INTEGER,
    "id" TEXT,
    "graph" TEXT,
    "kind" TEXT,
    "master" TEXT,
    "roomSet" TEXT,
    "problems" JSON
);
