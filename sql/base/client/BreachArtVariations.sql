-- Client table BreachArtVariations (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BreachArtVariations" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Portal" INTEGER,
    "Chest" INTEGER,
    "LargeChest" INTEGER,
    "Effect" INTEGER,
    "BuffVisualSmall" INTEGER,
    "BuffVisualBig" INTEGER,
    "LargeChestMarker" INTEGER,
    "SoulerCoaster" JSON,
    "CloseEffect" INTEGER,
    "Encounter" INTEGER
);
