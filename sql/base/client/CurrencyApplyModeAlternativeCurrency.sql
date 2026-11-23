-- Client table CurrencyApplyModeAlternativeCurrency (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CurrencyApplyModeAlternativeCurrency" (
    "_row" INTEGER PRIMARY KEY,
    "CurrencyItem" INTEGER,
    "AltCurrencyUse" INTEGER
);
