-- Client table MonsterPackEntries (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MonsterPackEntries" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MonsterPacksKey" INTEGER,
    "Flag" BOOLEAN,
    "Weight" INTEGER,
    "MonsterVarietiesKey" INTEGER,
    "__col5" TEXT
);
