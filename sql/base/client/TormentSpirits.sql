-- Client table TormentSpirits (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "TormentSpirits" (
    "_row" INTEGER PRIMARY KEY,
    "MonsterVarietiesKey" INTEGER,
    "Spirit_ModsKeys" JSON,
    "Touched_ModsKeys" JSON,
    "Possessed_ModsKeys" JSON,
    "MinZoneLevel" INTEGER,
    "MaxZoneLevel" INTEGER,
    "SpawnWeight" INTEGER,
    "SummonedMonster_MonsterVarietiesKey" INTEGER,
    "__col8" INTEGER,
    "ModsKeys0" JSON,
    "ModsKeys1" JSON,
    "PlayerPossessedMod" INTEGER,
    "Animation" INTEGER
);
