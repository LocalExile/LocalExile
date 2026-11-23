-- Client table NecropolisCraftingMods (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "NecropolisCraftingMods" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" INTEGER,
    "Stats" JSON,
    "StatsValues" JSON,
    "CraftingItemType" INTEGER,
    "__col5" INTEGER,
    "CorpsesNotConsumed" BOOLEAN,
    "CraftingItemTypes" JSON,
    "Achievements" JSON,
    "MatchingMonsters" INTEGER
);
