-- Client table EnvironmentTransitions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "EnvironmentTransitions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "OTFiles" JSON,
    "__col2" INTEGER,
    "Environment" JSON,
    "__col4" JSON,
    "__col5" INTEGER
);
