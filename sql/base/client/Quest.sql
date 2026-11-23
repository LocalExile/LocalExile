-- Client table Quest (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Quest" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Act" INTEGER,
    "Name" TEXT,
    "Icon_DDSFile" TEXT,
    "QuestId" INTEGER,
    "__col5" BOOLEAN,
    "Type" INTEGER,
    "__col7" JSON,
    "__col8" INTEGER,
    "TrackerGroup" JSON,
    "__col10" BOOLEAN,
    "QuestFlag" INTEGER
);
