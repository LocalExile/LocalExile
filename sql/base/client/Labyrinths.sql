-- Client table Labyrinths (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Labyrinths" (
    "_row" INTEGER PRIMARY KEY,
    "Tier" INTEGER,
    "Name" TEXT,
    "OfferingItem" INTEGER,
    "QuestFlag" INTEGER,
    "RequiredTrials" JSON,
    "AreaLevel" INTEGER,
    "__col6" INTEGER,
    "JewelReward" INTEGER,
    "__col8" JSON,
    "__col9" JSON,
    "MinLevel" INTEGER,
    "__col11" INTEGER,
    "__col12" INTEGER,
    "CraftingFontDescription" INTEGER
);
