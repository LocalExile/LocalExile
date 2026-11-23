-- Client table Tips (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Tips" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Text" TEXT,
    "TextXBox" TEXT,
    "MinLevel" INTEGER,
    "MaxLevel" INTEGER,
    "__col5" INTEGER,
    "__col6" INTEGER,
    "__col7" JSON,
    "__col8" INTEGER
);
