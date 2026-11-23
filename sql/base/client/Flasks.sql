-- Client table Flasks (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Flasks" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" INTEGER,
    "Name" TEXT,
    "Type" INTEGER,
    "LifePerUse" INTEGER,
    "ManaPerUse" INTEGER,
    "RecoveryTime" INTEGER,
    "BuffDefinitionsKey" INTEGER,
    "BuffStatValues" JSON,
    "RecoveryTime2" INTEGER,
    "BuffStatValues2" JSON
);
