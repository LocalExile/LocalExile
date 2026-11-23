-- Client table HarvestSeeds (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HarvestSeeds" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Monster" INTEGER,
    "Tier" INTEGER,
    "__col3" INTEGER,
    "__col4" JSON,
    "Achievements" JSON,
    "SeedAnimation" TEXT,
    "__col7" INTEGER,
    "HatchAnimation" TEXT,
    "SeedType" INTEGER,
    "__col10" BOOLEAN
);
