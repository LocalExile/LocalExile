-- Generated table area_transition_connection_status (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "area_transition_connection_status" (
    "_row" INTEGER PRIMARY KEY,
    "area" INTEGER,
    "ord" INTEGER,
    "index" INTEGER,
    "id" TEXT,
    "status" TEXT,
    "topologies" INTEGER,
    "pointOnly" INTEGER,
    "disabledOnly" INTEGER,
    "of" INTEGER,
    "reasons" JSON
);
