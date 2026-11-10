-- Client table Races (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Races" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Mods" JSON,
    "__col2" TEXT,
    "__col3" TEXT,
    "__col4" INTEGER,
    "__col5" INTEGER,
    "Area" JSON,
    "__col7" BOOLEAN,
    "__col8" INTEGER,
    "__col9" JSON,
    "ModFamily" JSON
);
