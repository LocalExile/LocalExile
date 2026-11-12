-- Generated table area_transition_connections (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "area_transition_connections" (
    "_row" INTEGER PRIMARY KEY,
    "area" INTEGER,
    "ord" INTEGER,
    "index" INTEGER,
    "id" TEXT,
    "name" TEXT,
    "hash16" INTEGER
);
