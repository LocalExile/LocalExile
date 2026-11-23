-- Client table NecropolisPacks (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "NecropolisPacks" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "Icon" TEXT,
    "Description" TEXT,
    "PackLeader1" TEXT,
    "PackLeader2" TEXT,
    "Mod" INTEGER
);
