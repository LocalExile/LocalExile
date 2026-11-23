-- Client table ItemNoteCode (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ItemNoteCode" (
    "_row" INTEGER PRIMARY KEY,
    "CurrencyItem" INTEGER,
    "Code" TEXT,
    "__col2" BOOLEAN,
    "__col3" BOOLEAN
);
