-- Client table BreachStashTabSubStashGroup (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BreachStashTabSubStashGroup" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "Group" JSON,
    "Icon" TEXT,
    "__col4" TEXT,
    "__col5" BOOLEAN,
    "__col6" REAL
);
