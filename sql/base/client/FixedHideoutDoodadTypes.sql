-- Client table FixedHideoutDoodadTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "FixedHideoutDoodadTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "HideoutDoodadsKeys" JSON,
    "BaseTypeHideoutDoodadsKey" INTEGER,
    "__col3" INTEGER,
    "__col4" INTEGER,
    "__col5" BOOLEAN,
    "UnlockQuest" INTEGER,
    "WorldArea" INTEGER
);
