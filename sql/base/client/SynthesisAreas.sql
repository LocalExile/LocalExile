-- Client table SynthesisAreas (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SynthesisAreas" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MinLevel" INTEGER,
    "MaxLevel" INTEGER,
    "Weight" INTEGER,
    "TopologiesKey" INTEGER,
    "MonsterPacksKeys" JSON,
    "ArtFile" TEXT,
    "Name" TEXT,
    "SynthesisAreaSizeKey" INTEGER,
    "AchievementItemsKey" JSON
);
