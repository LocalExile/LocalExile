-- Client table MicrotransactionCombineFormula (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MicrotransactionCombineFormula" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Result_Mtx" INTEGER,
    "Ingredients_Mtx" JSON,
    "BK2File" TEXT,
    "__col4" JSON,
    "__col5" INTEGER,
    "__col6" BOOLEAN
);
