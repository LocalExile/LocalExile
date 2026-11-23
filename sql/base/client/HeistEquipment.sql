-- Client table HeistEquipment (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HeistEquipment" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" INTEGER,
    "RequiredJob_HeistJobsKey" INTEGER,
    "RequiredLevel" INTEGER
);
