-- Client table ProjectilesArtVariations (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ProjectilesArtVariations" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Variant" INTEGER,
    "Projectiles" JSON,
    "__col3" INTEGER,
    "Stat" JSON
);
