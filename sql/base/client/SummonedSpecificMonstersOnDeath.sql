-- Client table SummonedSpecificMonstersOnDeath (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SummonedSpecificMonstersOnDeath" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MonsterVarietiesKey" INTEGER,
    "__col2" INTEGER,
    "MiscAnimated" INTEGER,
    "__col4" INTEGER,
    "__col5" BOOLEAN
);
