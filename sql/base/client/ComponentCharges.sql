-- Client table ComponentCharges (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ComponentCharges" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" TEXT,
    "MaxCharges" INTEGER,
    "PerCharge" INTEGER,
    "MaxCharges2" INTEGER,
    "PerCharge2" INTEGER
);
