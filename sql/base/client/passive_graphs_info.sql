-- Generated table passive_graphs_info (new in 3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "passive_graphs_info" (
    "_row" INTEGER PRIMARY KEY,
    "revision" TEXT,
    "analysisOnly" BOOLEAN,
    "mergedGraphFile" TEXT,
    "originalPhreciaSha256" TEXT
);
