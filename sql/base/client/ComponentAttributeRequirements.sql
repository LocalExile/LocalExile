-- Client table ComponentAttributeRequirements (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ComponentAttributeRequirements" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" TEXT,
    "ReqStr" INTEGER,
    "ReqDex" INTEGER,
    "ReqInt" INTEGER
);
