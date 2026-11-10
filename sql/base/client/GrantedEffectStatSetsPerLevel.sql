-- Client table GrantedEffectStatSetsPerLevel (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "GrantedEffectStatSetsPerLevel" (
    "_row" INTEGER PRIMARY KEY,
    "StatSet" INTEGER,
    "GemLevel" INTEGER,
    "PlayerLevelReq" INTEGER,
    "SpellCritChance" INTEGER,
    "AttackCritChance" INTEGER,
    "BaseMultiplier" INTEGER,
    "DamageEffectiveness" INTEGER,
    "BaseResolvedValues" JSON,
    "AdditionalStatsValues" JSON,
    "GrantedEffects" JSON,
    "AdditionalFlags" JSON,
    "FloatStats" JSON,
    "InterpolationBases" JSON,
    "AdditionalStats" JSON,
    "StatInterpolations" JSON,
    "FloatStatsValues" JSON
);
