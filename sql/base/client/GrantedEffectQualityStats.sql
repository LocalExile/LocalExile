-- Client table GrantedEffectQualityStats (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "GrantedEffectQualityStats" (
    "_row" INTEGER PRIMARY KEY,
    "GrantedEffectsKey" INTEGER,
    "StatsKeys" JSON,
    "StatsValuesPermille" JSON,
    "__col3" JSON,
    "__col4" JSON
);
