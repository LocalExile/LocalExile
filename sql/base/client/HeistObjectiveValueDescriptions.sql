-- Client table HeistObjectiveValueDescriptions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HeistObjectiveValueDescriptions" (
    "_row" INTEGER PRIMARY KEY,
    "Tier" INTEGER,
    "ValueMultiLessThan" REAL,
    "Description" TEXT
);
