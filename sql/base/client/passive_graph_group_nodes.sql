-- Generated table passive_graph_group_nodes (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "passive_graph_group_nodes" (
    "_row" INTEGER PRIMARY KEY,
    "grp" INTEGER,
    "ord" INTEGER,
    "id" INTEGER,
    "orbit" INTEGER,
    "orbitIndex" INTEGER
);
