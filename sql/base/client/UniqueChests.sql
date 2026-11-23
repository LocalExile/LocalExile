-- Client table UniqueChests (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "UniqueChests" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "WordsKey" INTEGER,
    "FlavourTextKey" INTEGER,
    "MinLevel" INTEGER,
    "ModsKeys" JSON,
    "SpawnWeight" INTEGER,
    "__col6" JSON,
    "AOFile" TEXT,
    "__col8" BOOLEAN,
    "__col9" JSON,
    "AppearanceChestsKey" INTEGER,
    "ChestsKey" INTEGER,
    "Achievement" JSON
);
