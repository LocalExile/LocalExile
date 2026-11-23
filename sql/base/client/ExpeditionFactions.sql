-- Client table ExpeditionFactions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ExpeditionFactions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "FactionFlag" TEXT,
    "__col3" INTEGER,
    "FactionIcon" TEXT,
    "MonsterVarieties" INTEGER,
    "Progress1" INTEGER,
    "Progress2Vaal" INTEGER,
    "Progress3Final" INTEGER,
    "Tags" INTEGER
);
