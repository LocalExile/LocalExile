-- Client table NearbyMonsterConditions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "NearbyMonsterConditions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MonsterVarietiesKeys" JSON,
    "MonsterAmount" INTEGER,
    "__col3" INTEGER,
    "IsNegated" BOOLEAN,
    "__col5" INTEGER,
    "__col6" JSON,
    "IsLessThen" BOOLEAN,
    "MinimumHealthPercentage" INTEGER
);
