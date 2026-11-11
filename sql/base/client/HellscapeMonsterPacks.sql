-- Client table HellscapeMonsterPacks (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HellscapeMonsterPacks" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "MonsterPack" INTEGER,
    "Faction" INTEGER,
    "IsDonutPack" BOOLEAN,
    "IsElite" BOOLEAN,
    "MinLevel" INTEGER,
    "MaxLevel" INTEGER
);
