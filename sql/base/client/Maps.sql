-- Client table Maps (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Maps" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" INTEGER,
    "Regular_WorldAreasKey" INTEGER,
    "Unique_WorldAreasKey" INTEGER,
    "MapUpgrade_BaseItemTypesKey" INTEGER,
    "MonsterPacksKeys" JSON,
    "AchievementItem" INTEGER,
    "Tier" INTEGER,
    "Shaped_Base_MapsKey" INTEGER,
    "Shaped_AreaLevel" INTEGER,
    "UpgradedFrom_MapsKey" INTEGER,
    "MapsKey2" INTEGER,
    "MapsKey3" INTEGER,
    "MapGeneration" INTEGER,
    "__col13" BOOLEAN,
    "__col14" BOOLEAN,
    "__col15" BOOLEAN
);
