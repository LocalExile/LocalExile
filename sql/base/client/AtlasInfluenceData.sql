-- Client table AtlasInfluenceData (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AtlasInfluenceData" (
    "_row" INTEGER PRIMARY KEY,
    "InfluencePack" INTEGER,
    "MonsterPacks" JSON,
    "BuffDefinition" INTEGER,
    "__col3" JSON,
    "BuffStatValues" JSON,
    "__col5" JSON,
    "__col6" INTEGER,
    "__col7" INTEGER,
    "InfluencePackSizeStat" INTEGER,
    "__col9" INTEGER,
    "__col10" INTEGER,
    "__col11" INTEGER,
    "__col12" INTEGER,
    "__col13" BOOLEAN,
    "__col14" BOOLEAN,
    "__col15" BOOLEAN,
    "__col16" BOOLEAN,
    "ZanaInfluenceChanceStat" INTEGER,
    "ZanaInfluenceMagicChanceStat" INTEGER,
    "ZanaInfluenceRareChanceStat" INTEGER
);
