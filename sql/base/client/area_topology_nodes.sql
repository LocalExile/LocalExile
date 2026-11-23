-- Generated table area_topology_nodes (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "area_topology_nodes" (
    "_row" INTEGER PRIMARY KEY,
    "topology" INTEGER,
    "ord" INTEGER,
    "index" INTEGER,
    "tag" TEXT,
    "rotation" TEXT,
    "names" JSON,
    "rooms" JSON,
    "subGraph" TEXT
);
