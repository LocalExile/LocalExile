-- Client table SanctumDefenceIcons (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SanctumDefenceIcons" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Stat" INTEGER,
    "DefenceIcon" TEXT,
    "DefenceBrokenIcon" TEXT,
    "BrokenStat" INTEGER,
    "Description" TEXT
);
