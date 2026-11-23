-- Client table ExpeditionTerrainFeatures (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ExpeditionTerrainFeatures" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "ExtraFeature" INTEGER,
    "ExpeditionFaction" INTEGER,
    "MinLevel" INTEGER,
    "MaxLevel" INTEGER,
    "__col5" INTEGER,
    "Area" INTEGER,
    "ExpeditionAreas" JSON,
    "__col8" INTEGER,
    "__col9" BOOLEAN,
    "UnearthAchievements" JSON
);
