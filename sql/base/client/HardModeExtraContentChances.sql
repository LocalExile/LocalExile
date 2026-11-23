-- Client table HardModeExtraContentChances (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HardModeExtraContentChances" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "SingleSpawnChance" INTEGER,
    "DoubleSpawnChance" INTEGER,
    "TripleSpawnChance" INTEGER,
    "NoSpawnChance" INTEGER,
    "Leagues" JSON,
    "__col6" BOOLEAN
);
