-- Client table BattlePasses (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BattlePasses" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "LeagueCategory" INTEGER,
    "International_BK2File" TEXT,
    "China_BK2File" TEXT,
    "MapCompletionCount" INTEGER,
    "__col5" BOOLEAN,
    "Mtx" INTEGER
);
