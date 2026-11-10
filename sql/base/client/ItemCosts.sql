-- Client table ItemCosts (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ItemCosts" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Cost1Currencies" JSON,
    "Cost1Values" JSON,
    "Cost2Currencies" JSON,
    "Cost2Values" JSON,
    "Cost3Currencies" JSON,
    "Cost3Values" JSON,
    "Cost4Currencies" JSON,
    "Cost4Values" JSON,
    "RuthlessWithGold" JSON
);
