-- Client table TieredMicrotransactions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "TieredMicrotransactions" (
    "_row" INTEGER PRIMARY KEY,
    "MTX" INTEGER,
    "TierThresholds" JSON,
    "__col2" INTEGER,
    "__col3" JSON,
    "__col4" INTEGER,
    "TierCount" INTEGER,
    "__col6" JSON,
    "__col7" JSON,
    "__col8" JSON,
    "__col9" BOOLEAN,
    "__col10" BOOLEAN,
    "__col11" INTEGER,
    "__col12" BOOLEAN
);
