-- Client table NPCAudio (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "NPCAudio" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Audio" JSON,
    "__col2" JSON,
    "VolumePercentage" INTEGER,
    "QuestFlag1" INTEGER,
    "QuestFlag2" INTEGER,
    "__col6" INTEGER,
    "__col7" INTEGER
);
