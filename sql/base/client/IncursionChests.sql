-- Client table IncursionChests (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "IncursionChests" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "ChestsKey" INTEGER,
    "UniqueChestsKey" INTEGER,
    "MinLevel" INTEGER,
    "MaxLevel" INTEGER,
    "Weight" INTEGER,
    "__col6" INTEGER
);
