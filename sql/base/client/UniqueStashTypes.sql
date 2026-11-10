-- Client table UniqueStashTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "UniqueStashTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Order" INTEGER,
    "Width" INTEGER,
    "Height" INTEGER,
    "TotalCount" INTEGER,
    "__col5" INTEGER,
    "Name" TEXT,
    "StandardCount" INTEGER,
    "Image" TEXT,
    "ChallengeLeagueCount" INTEGER,
    "IsDisabled" BOOLEAN
);
