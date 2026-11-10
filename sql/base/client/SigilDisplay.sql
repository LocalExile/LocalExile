-- Client table SigilDisplay (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SigilDisplay" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Active_StatsKey" INTEGER,
    "Inactive_StatsKey" INTEGER,
    "DDSFile" TEXT,
    "Inactive_ArtFile" TEXT,
    "Active_ArtFile" TEXT,
    "Frame_ArtFile" TEXT
);
