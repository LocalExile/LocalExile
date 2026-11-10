-- Client table BossKillEvent (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BossKillEvent" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "QuestFlag" INTEGER,
    "AreaLevel" INTEGER,
    "Monster" INTEGER,
    "__col4" JSON,
    "__col5" INTEGER,
    "QuestFlag2" INTEGER,
    "__col7" INTEGER,
    "__col8" INTEGER,
    "__col9" INTEGER
);
