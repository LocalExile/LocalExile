-- Client table ArchnemesisModVisuals (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ArchnemesisModVisuals" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MiscAnimated" INTEGER,
    "BuffVisualOrbType" INTEGER,
    "MiscEffectPack" INTEGER,
    "BuffVisual" JSON,
    "Monster" JSON,
    "MonsterMiscAnimated" JSON,
    "MonsterEffectPack" JSON
);
