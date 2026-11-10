-- Client table AlternateTreeArt (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AlternateTreeArt" (
    "_row" INTEGER PRIMARY KEY,
    "TreeVersion" INTEGER,
    "ConnectionArt" INTEGER,
    "KeystoneFrameArt" INTEGER,
    "RadiiArt" INTEGER,
    "NotableFrameArt" INTEGER,
    "AscendancyFrameArt" INTEGER
);
