-- Client table LeagueInfo (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "LeagueInfo" (
    "_row" INTEGER PRIMARY KEY,
    "PanelVersion" INTEGER,
    "PanelId" TEXT,
    "PanelImage" TEXT,
    "HeaderImage" TEXT,
    "Screenshots" JSON,
    "Description" TEXT,
    "__col6" BOOLEAN,
    "TrailerVideoLink" TEXT,
    "BackgroundImage" TEXT,
    "__col9" BOOLEAN,
    "__col10" BOOLEAN,
    "PanelItems" JSON,
    "__col12" TEXT,
    "__col13" TEXT,
    "__col14" JSON,
    "__col15" TEXT,
    "__col16" BOOLEAN,
    "__col17" INTEGER
);
