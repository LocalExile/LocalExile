-- Client table AdditionalMonstersFromMapStats (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AdditionalMonstersFromMapStats" (
    "_row" INTEGER PRIMARY KEY,
    "Stat" INTEGER,
    "Monsters" JSON,
    "__col2" INTEGER,
    "__col3" BOOLEAN,
    "__col4" INTEGER
);
