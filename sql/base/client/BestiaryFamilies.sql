-- Client table BestiaryFamilies (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BestiaryFamilies" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "Icon" TEXT,
    "IconSmall" TEXT,
    "Illustration" TEXT,
    "PageArt" TEXT,
    "FlavourText" TEXT,
    "__col7" BOOLEAN,
    "TagsKey" INTEGER,
    "__col9" INTEGER,
    "ModsKeys" JSON,
    "CurrencyItemsKey" INTEGER
);
