-- Client table SentinelPassives (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SentinelPassives" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "HASH16" INTEGER,
    "__col2" TEXT,
    "Stats" JSON,
    "__col4" JSON,
    "__col5" INTEGER,
    "__col6" INTEGER,
    "PassiveType" INTEGER,
    "__col8" INTEGER,
    "__col9" INTEGER,
    "__col10" INTEGER
);
