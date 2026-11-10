-- Client table ElderMapBossOverride (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ElderMapBossOverride" (
    "_row" INTEGER PRIMARY KEY,
    "WorldAreasKey" INTEGER,
    "MonsterVarietiesKeys" JSON,
    "TerrainMetadata" TEXT
);
