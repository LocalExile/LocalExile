-- Client table StampChoice (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "StampChoice" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Room" INTEGER,
    "__col2" INTEGER,
    "Effect" TEXT,
    "Icon" TEXT,
    "StampFamily" INTEGER,
    "Achievements" JSON,
    "HASH16" INTEGER
);
