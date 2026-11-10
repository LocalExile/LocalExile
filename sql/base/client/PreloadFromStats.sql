-- Client table PreloadFromStats (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "PreloadFromStats" (
    "_row" INTEGER PRIMARY KEY,
    "Stat" INTEGER,
    "Animation" JSON,
    "Object" JSON,
    "BuffVisual" JSON,
    "EffectPack" JSON,
    "__col5" INTEGER,
    "GroundEffect" JSON
);
