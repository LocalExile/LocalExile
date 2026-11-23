-- Client table MtxTypeGameSpecific (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MtxTypeGameSpecific" (
    "_row" INTEGER PRIMARY KEY,
    "Type" INTEGER,
    "HASH32" INTEGER,
    "DDSFile" TEXT,
    "Category" INTEGER,
    "__col4" JSON,
    "__col5" INTEGER,
    "__col6" JSON
);
