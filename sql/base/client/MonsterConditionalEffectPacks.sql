-- Client table MonsterConditionalEffectPacks (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MonsterConditionalEffectPacks" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MiscEffectPack1" JSON,
    "MiscEffectPack2" JSON,
    "MiscEffectPack3" JSON,
    "MiscEffectPack4" JSON,
    "__col5" INTEGER
);
