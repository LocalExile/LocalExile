-- Client table QuestItems (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "QuestItems" (
    "_row" INTEGER PRIMARY KEY,
    "Item" INTEGER,
    "TriggeredQuestFlag" INTEGER,
    "__col2" INTEGER,
    "__col3" INTEGER,
    "__col4" JSON,
    "__col5" BOOLEAN,
    "__col6" BOOLEAN,
    "HelpText" INTEGER,
    "Description" INTEGER,
    "Script" TEXT,
    "__col10" JSON,
    "__col11" JSON,
    "__col12" INTEGER
);
