-- Generated table passive_graphs (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "passive_graphs" (
    "_row" INTEGER PRIMARY KEY,
    "graph" TEXT,
    "source" TEXT,
    "sha256" TEXT,
    "roots" JSON,
    "orbits" JSON
);
