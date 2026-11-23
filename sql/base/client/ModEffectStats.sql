-- Client table ModEffectStats (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ModEffectStats" (
    "_row" INTEGER PRIMARY KEY,
    "StatsKey" INTEGER,
    "TagsKeys" JSON,
    "ApplyToExplicit" BOOLEAN,
    "__col3" INTEGER,
    "ApplyToImplicit" BOOLEAN,
    "ApplyToPrefix" BOOLEAN,
    "ApplyToSuffix" BOOLEAN,
    "Multiplier" INTEGER,
    "ApplyToEnchant" BOOLEAN
);
