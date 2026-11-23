-- Client table IncursionChestRewards (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "IncursionChestRewards" (
    "_row" INTEGER PRIMARY KEY,
    "IncursionRoomsKey" INTEGER,
    "IncursionChestsKeys" JSON,
    "ChestMarkerMetadata" TEXT,
    "HASH32" INTEGER,
    "RuthlessIncursionChestsKeys" JSON,
    "__col5" INTEGER,
    "__col6" INTEGER,
    "__col7" INTEGER,
    "__col8" INTEGER
);
