-- Client table ExpeditionDeals (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ExpeditionDeals" (
    "_row" INTEGER PRIMARY KEY,
    "Id" INTEGER,
    "Function" TEXT,
    "Arguments" TEXT,
    "TextAudio" INTEGER,
    "Description" TEXT,
    "BuyAchievements" JSON,
    "NPCShop" INTEGER,
    "DealFamily" INTEGER
);
