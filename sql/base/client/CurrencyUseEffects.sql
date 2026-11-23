-- Client table CurrencyUseEffects (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CurrencyUseEffects" (
    "_row" INTEGER PRIMARY KEY,
    "CurrencyItem" INTEGER,
    "__col1" INTEGER,
    "BK2File" TEXT,
    "__col3" BOOLEAN,
    "BK2File2" TEXT,
    "__col5" BOOLEAN,
    "Stat" INTEGER,
    "__col7" REAL,
    "SoundEffect" INTEGER
);
