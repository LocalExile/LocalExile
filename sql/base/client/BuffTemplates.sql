-- Client table BuffTemplates (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BuffTemplates" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "BuffDefinitionsKey" INTEGER,
    "Buff_StatValues" JSON,
    "AuraRadius" INTEGER,
    "__col4" JSON,
    "__col5" JSON,
    "BuffVisualsKey" INTEGER,
    "__col7" REAL,
    "__col8" BOOLEAN,
    "StatsKey" JSON,
    "__col10" INTEGER,
    "__col11" INTEGER,
    "__col12" BOOLEAN,
    "__col13" INTEGER
);
