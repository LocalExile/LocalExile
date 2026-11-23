-- Client table DelveRobotVariations (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DelveRobotVariations" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "AOFile" TEXT,
    "Joint" JSON,
    "Pet" INTEGER,
    "MiscAnimated" INTEGER
);
