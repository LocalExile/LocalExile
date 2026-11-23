-- Client table Ascendancy (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Ascendancy" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "ClassNo" INTEGER,
    "Character" INTEGER,
    "CoordinateRect" TEXT,
    "RGBFlavourTextColour" TEXT,
    "Name" TEXT,
    "FlavourText" TEXT,
    "OGGFile" TEXT,
    "PassiveTreeImage" TEXT,
    "__col9" INTEGER,
    "__col10" INTEGER,
    "BackgroundImage" TEXT,
    "LeagueName" TEXT,
    "LeagueFlavourText" TEXT,
    "LeagueBackgroundImage" TEXT,
    "LeaguePassiveTreeImage" TEXT,
    "__col16" INTEGER,
    "__col17" INTEGER,
    "LeagueIcon" TEXT,
    "__col19" BOOLEAN
);
