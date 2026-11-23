-- Client table AtlasMissionTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AtlasMissionTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Stats" JSON,
    "StatValues" JSON,
    "Astrolabe" INTEGER,
    "__col4" INTEGER,
    "__col5" INTEGER,
    "__col6" JSON
);
