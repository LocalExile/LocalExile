-- Client table MercenaryClasses (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MercenaryClasses" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MonsterVariety" INTEGER,
    "__col2" INTEGER,
    "TerrainFeature" INTEGER,
    "__col4" INTEGER,
    "__col5" INTEGER,
    "ClassIcon" TEXT,
    "HouseIcon" TEXT,
    "Attribute" INTEGER,
    "HouseBuffIcon" TEXT,
    "MonsterVarietyAllied" INTEGER,
    "HouseSpawnChanceStats" JSON,
    "AttributeSpawnChanceStats" JSON,
    "HouseName" TEXT
);
