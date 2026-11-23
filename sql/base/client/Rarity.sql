-- Client table Rarity (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Rarity" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MinMods" INTEGER,
    "MaxMods" INTEGER,
    "__col3" INTEGER,
    "MaxPrefix" INTEGER,
    "__col5" INTEGER,
    "MaxSuffix" INTEGER,
    "Color" TEXT,
    "Text" TEXT
);
