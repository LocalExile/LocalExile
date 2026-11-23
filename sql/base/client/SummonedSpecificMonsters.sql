-- Client table SummonedSpecificMonsters (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SummonedSpecificMonsters" (
    "_row" INTEGER PRIMARY KEY,
    "Id" INTEGER,
    "MonsterVarietiesKey" INTEGER,
    "GameObjectRegisterKey" INTEGER,
    "SummonEffect" INTEGER,
    "__col4" BOOLEAN,
    "__col5" BOOLEAN,
    "__col6" INTEGER,
    "__col7" INTEGER,
    "__col8" BOOLEAN,
    "BuffDefinition" INTEGER,
    "BuffVisual" JSON,
    "__col11" INTEGER,
    "__col12" BOOLEAN,
    "__col13" INTEGER,
    "__col14" TEXT,
    "__col15" BOOLEAN,
    "__col16" BOOLEAN,
    "__col17" INTEGER
);
