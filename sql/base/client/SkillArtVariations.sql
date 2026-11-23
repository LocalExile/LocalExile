-- Client table SkillArtVariations (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SkillArtVariations" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "AnimatedArt" JSON,
    "BeamArt" JSON,
    "EffectPacksArt" JSON,
    "ObjectArt" JSON,
    "Variants" JSON,
    "BuffVisuals" JSON,
    "__col7" JSON,
    "ProjectileArt" JSON,
    "Stat" INTEGER,
    "MonsterVarieties" JSON,
    "ActiveSkill" JSON,
    "GrantedEffect" JSON,
    "Mtx" JSON
);
