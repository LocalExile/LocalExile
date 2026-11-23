-- Client table MiscAnimatedArtVariations (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MiscAnimatedArtVariations" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Animation" JSON,
    "__col2" INTEGER,
    "Variant" INTEGER,
    "Stats" INTEGER
);
