-- Client table BetrayalTraitorRewards (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BetrayalTraitorRewards" (
    "_row" INTEGER PRIMARY KEY,
    "BetrayalJobsKey" INTEGER,
    "BetrayalTargetsKey" INTEGER,
    "BetrayalRanksKey" INTEGER,
    "Description" TEXT,
    "Description2" TEXT
);
