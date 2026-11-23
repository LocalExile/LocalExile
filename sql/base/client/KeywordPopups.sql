-- Client table KeywordPopups (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "KeywordPopups" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Term" TEXT,
    "Definition" TEXT,
    "__col3" TEXT,
    "__col4" TEXT,
    "ItemReference" INTEGER,
    "ModReference" JSON
);
