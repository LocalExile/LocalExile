-- Client table CurrencyExchange (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CurrencyExchange" (
    "_row" INTEGER PRIMARY KEY,
    "Item" INTEGER,
    "Category" INTEGER,
    "SubCategory" INTEGER,
    "EnabledInStandardLeague" BOOLEAN,
    "EnabledInChallengeLeague" BOOLEAN,
    "GoldPurchaseFee" INTEGER,
    "__col6" BOOLEAN,
    "__col7" BOOLEAN
);
