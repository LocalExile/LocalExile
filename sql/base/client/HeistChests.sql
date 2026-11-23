-- Client table HeistChests (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HeistChests" (
    "_row" INTEGER PRIMARY KEY,
    "ChestsKey" INTEGER,
    "Weight" INTEGER,
    "HeistAreasKey" JSON,
    "HeistChestTypesKey" INTEGER
);
