-- Client table HellscapePassives (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HellscapePassives" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "Stats" JSON,
    "StatsValues" JSON,
    "Points" INTEGER,
    "HASH16" INTEGER,
    "Icon" TEXT,
    "IconMaxed" TEXT,
    "SoundEffect" INTEGER,
    "__col9" INTEGER,
    "Quest" INTEGER
);
