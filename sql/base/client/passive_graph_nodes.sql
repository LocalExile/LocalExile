-- Generated table passive_graph_nodes (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "passive_graph_nodes" (
    "_row" INTEGER PRIMARY KEY,
    "graph" INTEGER,
    "ord" INTEGER,
    "id" INTEGER,
    "adjacent" JSON
);
