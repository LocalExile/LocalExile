-- Client table BuffVisualOrbs (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BuffVisualOrbs" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "BuffVisualOrbTypesKey" INTEGER,
    "BuffVisualOrbArtKeys" JSON,
    "Player_BuffVisualOrbArtKeys" JSON,
    "BuffVisualOrbArtKeys2" JSON
);
