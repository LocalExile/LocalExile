-- Client table Tutorial (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Tutorial" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "UIFile" TEXT,
    "ClientString" INTEGER,
    "IsEnabled" BOOLEAN,
    "__col4" INTEGER,
    "QuestFlags" JSON,
    "QuestFlagComplete" INTEGER,
    "__col7" INTEGER,
    "__col8" JSON,
    "__col9" BOOLEAN,
    "__col10" BOOLEAN,
    "__col11" INTEGER
);
