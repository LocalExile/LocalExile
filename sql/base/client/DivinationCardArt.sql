-- Client table DivinationCardArt (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DivinationCardArt" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" INTEGER,
    "ArtFile" TEXT,
    "Effects" JSON,
    "MaskFile" TEXT
);
