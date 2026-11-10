-- Client table Environments (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Environments" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Base_ENVFile" TEXT,
    "Corrupted_ENVFiles" JSON,
    "QuestFlag1" JSON,
    "QuestFlag2" JSON,
    "__col5" JSON,
    "EnvironmentTransitionsKey" INTEGER,
    "PreloadGroup" INTEGER,
    "__col8" BOOLEAN,
    "__col9" INTEGER
);
