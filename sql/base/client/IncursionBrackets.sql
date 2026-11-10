-- Client table IncursionBrackets (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "IncursionBrackets" (
    "_row" INTEGER PRIMARY KEY,
    "MinLevel" INTEGER,
    "Incursion_WorldAreasKey" INTEGER,
    "Template_WorldAreasKey" INTEGER,
    "__col3" JSON,
    "__col4" REAL,
    "__col5" INTEGER
);
