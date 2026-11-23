-- Client table ExpeditionNPCs (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ExpeditionNPCs" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "NPCs" JSON,
    "RerollItem" INTEGER,
    "__col3" INTEGER,
    "__col4" INTEGER,
    "__col5" INTEGER,
    "Faction" INTEGER,
    "Reroll" INTEGER,
    "AllBombsPlaced" INTEGER,
    "BombPlacedRemnant" INTEGER,
    "BombPlacedTreasure" INTEGER,
    "BombPlacedMonsters" INTEGER,
    "BombPlacedGeneric" INTEGER,
    "EncounterComplete" INTEGER,
    "__col14" INTEGER,
    "__col15" INTEGER
);
