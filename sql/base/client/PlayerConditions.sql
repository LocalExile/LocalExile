-- Client table PlayerConditions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "PlayerConditions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "BuffDefinitionsKeys" JSON,
    "__col2" BOOLEAN,
    "BuffStacks" INTEGER,
    "CharactersKey" INTEGER,
    "StatsKeys" JSON,
    "__col6" BOOLEAN,
    "StatValue" INTEGER,
    "BaseItemType" JSON,
    "__col9" BOOLEAN
);
