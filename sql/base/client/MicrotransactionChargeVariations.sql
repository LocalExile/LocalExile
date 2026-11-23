-- Client table MicrotransactionChargeVariations (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MicrotransactionChargeVariations" (
    "_row" INTEGER PRIMARY KEY,
    "Mtx" INTEGER,
    "__col1" INTEGER,
    "PowerChargeVisual" INTEGER,
    "EnduranceChargeVisual" INTEGER,
    "FrenzyChargeVisual" INTEGER
);
