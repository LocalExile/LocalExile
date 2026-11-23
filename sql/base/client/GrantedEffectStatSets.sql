-- Client table GrantedEffectStatSets (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "GrantedEffectStatSets" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "ImplicitStats" JSON,
    "ConstantStats" JSON,
    "ConstantStatsValues" JSON,
    "BaseEffectiveness" REAL,
    "__col5" REAL,
    "IncrementalEffectiveness" REAL
);
